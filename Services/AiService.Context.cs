using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MSLX.Plugin.AiAgents.Utils;

namespace MSLX.Plugin.AiAgents.Services;

public sealed class AiContextStatus
{
    public int UsedTokens { get; init; }
    public int MaxTokens { get; init; }
    public string MaxLabel { get; init; } = ModelContextLengthHelper.DefaultContextLengthText;
    public double UsagePercent { get; init; }
    public bool IsEstimated { get; init; }
    public bool Compressed { get; init; }
    public int? CompressedBeforeTokens { get; init; }
    public int? CompressedAfterTokens { get; init; }
    public string? Summary { get; init; }
    public int ArchivedSourceCount { get; init; }
    public int CompressionThresholdTokens { get; init; }
}

public partial class AiService
{
    private const string ContextSummaryPrefix = "【历史对话上下文已自动压缩摘要】";
    private const string UiContextPrefix = "【当前用户界面上下文】";
    private const double CompressionTriggerRatio = 0.80;
    private const double CompressionTargetRatio = 0.50;

    private int? _cachedToolsTokens;

    public int GetBaseContextTokens()
    {
        var systemTokens = EstimateTextTokens(AiSkillManager.GetSystemPrompt()) + 4;
        var toolsTokens = GetToolsTokens();
        return systemTokens + toolsTokens + 3;
    }

    private int GetToolsTokens()
    {
        if (_cachedToolsTokens.HasValue) return _cachedToolsTokens.Value;
        var tokens = EstimateTextTokens(GetToolsDefinition().ToJsonString());
        _cachedToolsTokens = tokens;
        return tokens;
    }

    private static int GetCompressionThreshold(int maxTokens) =>
        Math.Max(1, (int)Math.Floor(maxTokens * CompressionTriggerRatio));

    private static AiContextStatus BuildContextStatus(
        int usedTokens,
        int maxTokens,
        string maxLabel,
        bool isEstimated,
        bool compressed = false,
        int? beforeTokens = null,
        int? afterTokens = null,
        string? summary = null,
        int archivedSourceCount = 0)
    {
        var percent = maxTokens > 0
            ? Math.Round(Math.Min(100d, usedTokens * 100d / maxTokens), 1)
            : 0d;

        return new AiContextStatus
        {
            UsedTokens = Math.Max(0, usedTokens),
            MaxTokens = maxTokens,
            MaxLabel = maxLabel,
            UsagePercent = percent,
            IsEstimated = isEstimated,
            Compressed = compressed,
            CompressedBeforeTokens = beforeTokens,
            CompressedAfterTokens = afterTokens,
            Summary = summary,
            ArchivedSourceCount = archivedSourceCount,
            CompressionThresholdTokens = GetCompressionThreshold(maxTokens)
        };
    }

    internal static int EstimateTextTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int asciiCount = 0;
        int cjkCount = 0;
        int otherNonAscii = 0;

        foreach (char ch in text)
        {
            if (ch < 0x80)
            {
                asciiCount++;
            }
            else if ((ch >= '\u4E00' && ch <= '\u9FFF') ||
                     (ch >= '\u3400' && ch <= '\u4DBF') ||
                     (ch >= '\u3000' && ch <= '\u303F') ||
                     (ch >= '\uFF00' && ch <= '\uFFEF'))
            {
                cjkCount++;
            }
            else
            {
                otherNonAscii++;
            }
        }

        return (int)Math.Ceiling(cjkCount * 1.35 + otherNonAscii * 1.5 + asciiCount * 0.28);
    }

    internal static int EstimateMessageTokens(JsonObject? msg)
    {
        if (msg == null) return 0;

        int tokens = 4;
        tokens += EstimateTextTokens(GetStringOrNull(msg["role"]));
        tokens += EstimateTextTokens(GetStringOrNull(msg["content"]));

        if (msg["tool_calls"] is JsonNode toolCallsNode)
        {
            tokens += EstimateTextTokens(toolCallsNode.ToJsonString());
        }

        var toolCallId = GetStringOrNull(msg["tool_call_id"]);
        if (!string.IsNullOrEmpty(toolCallId))
        {
            tokens += EstimateTextTokens(toolCallId);
        }

        return tokens;
    }

    internal static int EstimateMessagesTokens(IEnumerable<JsonNode?> messages)
    {
        int total = 0;
        foreach (var node in messages)
        {
            if (node is JsonObject obj)
            {
                total += EstimateMessageTokens(obj);
            }
        }
        return total;
    }

    private sealed class ContextMessageGroup
    {
        public List<JsonObject> Messages { get; } = new();
        public int Tokens { get; set; }
        public int NonSystemInputCount { get; set; }
    }

    private async Task<(bool Compressed, int BeforeTokens, int AfterTokens, int ArchivedSourceCount, string? Summary)>
        EnsureContextWithinLimitAsync(
            JsonArray reqMessages,
            int remainingInputNonSystemCount,
            int toolsTokens,
            int maxTokens,
            int? knownCurrentTokens,
            string requestUrl,
            string apiKey,
            string modelName,
            CancellationToken cancellationToken)
    {
        int beforeTokens = knownCurrentTokens ?? (EstimateMessagesTokens(reqMessages) + toolsTokens + 3);
        int threshold = GetCompressionThreshold(maxTokens);
        if (beforeTokens < threshold)
        {
            return (false, beforeTokens, beforeTokens, 0, null);
        }

        var preservedPrefix = new List<JsonObject>();
        string? priorSummary = null;
        var bodyItems = new List<(JsonObject Msg, bool IsFromInput)>();
        int seenInputNonSystem = 0;

        for (int i = 0; i < reqMessages.Count; i++)
        {
            if (reqMessages[i] is not JsonObject msgObj) continue;

            if (i == 0)
            {
                preservedPrefix.Add((JsonObject)msgObj.DeepClone());
                continue;
            }

            var role = GetStringOrNull(msgObj["role"]) ?? string.Empty;
            var content = GetStringOrNull(msgObj["content"]) ?? string.Empty;

            if (bodyItems.Count == 0 && role == "system")
            {
                if (content.StartsWith(ContextSummaryPrefix, StringComparison.Ordinal))
                {
                    var extracted = content[ContextSummaryPrefix.Length..].Trim();
                    priorSummary = string.IsNullOrEmpty(priorSummary)
                        ? extracted
                        : $"{priorSummary}\n{extracted}";
                }
                else
                {
                    preservedPrefix.Add((JsonObject)msgObj.DeepClone());
                }
                continue;
            }

            bool isFromInput = role != "system" && seenInputNonSystem < remainingInputNonSystemCount;
            if (isFromInput)
            {
                seenInputNonSystem++;
            }

            bodyItems.Add(((JsonObject)msgObj.DeepClone(), isFromInput));
        }

        if (bodyItems.Count == 0)
        {
            return (false, beforeTokens, beforeTokens, 0, null);
        }

        var groups = new List<ContextMessageGroup>();
        for (int i = 0; i < bodyItems.Count; i++)
        {
            var (msg, isFromInput) = bodyItems[i];
            var role = GetStringOrNull(msg["role"]) ?? string.Empty;
            var group = new ContextMessageGroup();
            group.Messages.Add(msg);
            group.Tokens += EstimateMessageTokens(msg);
            if (isFromInput)
            {
                group.NonSystemInputCount++;
            }

            if (role == "assistant" && msg["tool_calls"] is JsonArray toolCalls && toolCalls.Count > 0)
            {
                while (i + 1 < bodyItems.Count && GetStringOrNull(bodyItems[i + 1].Msg["role"]) == "tool")
                {
                    i++;
                    var toolMsg = bodyItems[i].Msg;
                    group.Messages.Add(toolMsg);
                    group.Tokens += EstimateMessageTokens(toolMsg);
                    if (bodyItems[i].IsFromInput)
                    {
                        group.NonSystemInputCount++;
                    }
                }
            }

            groups.Add(group);
        }

        int prefixTokens = EstimateMessagesTokens(preservedPrefix) + toolsTokens + 3;
        int targetTotal = (int)Math.Floor(maxTokens * CompressionTargetRatio);
        int summaryReserve = 600;
        int tailBudget = Math.Max(512, targetTotal - prefixTokens - summaryReserve);

        int keepStartIndex = groups.Count - 1;
        int keptTokens = groups[keepStartIndex].Tokens;
        for (int idx = groups.Count - 2; idx >= 0; idx--)
        {
            if (idx == 0)
            {
                // Always leave at least the oldest group to fold into the summary when compression triggers.
                break;
            }

            if (keptTokens + groups[idx].Tokens > tailBudget)
            {
                break;
            }

            keptTokens += groups[idx].Tokens;
            keepStartIndex = idx;
        }

        var olderGroups = groups.Take(keepStartIndex).ToList();
        var keptGroups = groups.Skip(keepStartIndex).ToList();

        string? newSummary = null;
        int archivedSourceCount = olderGroups.Sum(g => g.NonSystemInputCount);

        if (olderGroups.Count > 0)
        {
            newSummary = await SummarizeOlderGroupsAsync(
                priorSummary, olderGroups, maxTokens, requestUrl, apiKey, modelName, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(priorSummary))
        {
            newSummary = priorSummary;
        }

        // Compact oversized tool outputs inside keptGroups if still needed to get under threshold.
        int perToolCapTokens = Math.Max(1024, (int)Math.Floor(maxTokens * 0.25));
        int estimatedWithNewSummary = prefixTokens
            + (string.IsNullOrWhiteSpace(newSummary) ? 0 : EstimateTextTokens($"{ContextSummaryPrefix}\n{newSummary}") + 4)
            + keptGroups.Sum(g => g.Tokens);

        bool truncatedToolOutput = false;
        if (estimatedWithNewSummary >= threshold)
        {
            foreach (var group in keptGroups)
            {
                foreach (var msg in group.Messages)
                {
                    if (GetStringOrNull(msg["role"]) != "tool") continue;
                    var content = GetStringOrNull(msg["content"]);
                    if (string.IsNullOrEmpty(content)) continue;

                    if (EstimateTextTokens(content) > perToolCapTokens)
                    {
                        msg["content"] = TruncateMiddleText(content, perToolCapTokens);
                        truncatedToolOutput = true;
                    }
                }
            }
        }

        if (olderGroups.Count == 0 && !truncatedToolOutput)
        {
            return (false, beforeTokens, beforeTokens, 0, null);
        }

        var rebuilt = new List<JsonObject>();
        rebuilt.AddRange(preservedPrefix);
        if (!string.IsNullOrWhiteSpace(newSummary))
        {
            rebuilt.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = $"{ContextSummaryPrefix}\n{newSummary}"
            });
        }

        foreach (var group in keptGroups)
        {
            rebuilt.AddRange(group.Messages);
        }

        int afterTokens = EstimateMessagesTokens(rebuilt) + toolsTokens + 3;
        if (afterTokens >= beforeTokens && olderGroups.Count == 0)
        {
            return (false, beforeTokens, beforeTokens, 0, null);
        }

        reqMessages.Clear();
        foreach (var item in rebuilt)
        {
            reqMessages.Add(item);
        }

        _logger.LogInformation(
            "AI 上下文自动压缩完成: {Before} -> {After} tokens (上限 {Max}, 压缩消息组 {OlderCount}, 归档输入消息 {ArchivedCount})",
            beforeTokens, afterTokens, maxTokens, olderGroups.Count, archivedSourceCount);

        return (true, beforeTokens, afterTokens, archivedSourceCount, newSummary);
    }

    private async Task<string> SummarizeOlderGroupsAsync(
        string? priorSummary,
        List<ContextMessageGroup> olderGroups,
        int maxTokens,
        string requestUrl,
        string apiKey,
        string modelName,
        CancellationToken cancellationToken)
    {
        int transcriptBudgetTokens = Math.Clamp((int)Math.Floor(maxTokens * 0.45), 2000, 48000);
        int charBudget = transcriptBudgetTokens * 2;
        var transcript = BuildTranscriptForSummary(priorSummary, olderGroups, charBudget);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

            var payload = new JsonObject
            {
                ["model"] = modelName,
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "system",
                        ["content"] = "你是对话上下文压缩助手。请将给定的历史运维对话浓缩为精炼、结构化的中文摘要，供后续对话继续使用。必须保留：1) 用户目标与关键约束；2) 涉及的服务器 ID、名称、核心类型、版本号、Java 版本、关键路径；3) 已执行的工具操作与关键结果/报错；4) 尚未完成的任务或待确认事项。严禁编造信息，控制在 800 字以内。"
                    },
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = transcript
                    }
                },
                ["stream"] = false,
                ["max_tokens"] = 1200
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                var json = JsonNode.Parse(body)?.AsObject();
                var summaryText = GetStringOrNull(json?["choices"]?[0]?["message"]?["content"]);
                if (!string.IsNullOrWhiteSpace(summaryText))
                {
                    return summaryText.Trim();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "调用模型进行上下文自动压缩摘要失败，将回退至本地摘要压缩");
        }

        return BuildFallbackSummary(priorSummary, olderGroups);
    }

    private static string BuildTranscriptForSummary(string? priorSummary, List<ContextMessageGroup> olderGroups, int charBudget)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(priorSummary))
        {
            sb.AppendLine("[早期对话摘要]");
            sb.AppendLine(priorSummary.Trim());
            sb.AppendLine();
        }

        foreach (var group in olderGroups)
        {
            foreach (var msg in group.Messages)
            {
                if (sb.Length >= charBudget) break;

                var role = GetStringOrNull(msg["role"]) ?? "unknown";
                var content = GetStringOrNull(msg["content"]) ?? string.Empty;

                if (role == "assistant" && msg["tool_calls"] is JsonArray toolCalls && toolCalls.Count > 0)
                {
                    var names = toolCalls
                        .Select(tc => tc?["function"]?["name"]?.ToString())
                        .Where(n => !string.IsNullOrWhiteSpace(n));
                    sb.AppendLine($"[assistant 调用工具: {string.Join(", ", names)}]");
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        sb.AppendLine(TruncateSnippet(content, 400));
                    }
                }
                else if (role == "tool")
                {
                    sb.AppendLine($"[tool 返回]: {TruncateSnippet(content, 500)}");
                }
                else
                {
                    sb.AppendLine($"[{role}]: {TruncateSnippet(content, 1200)}");
                }
            }
            if (sb.Length >= charBudget) break;
        }

        return sb.ToString();
    }

    private static string BuildFallbackSummary(string? priorSummary, List<ContextMessageGroup> olderGroups)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(priorSummary))
        {
            sb.AppendLine("此前摘要：");
            sb.AppendLine(TruncateSnippet(priorSummary.Trim(), 800));
            sb.AppendLine();
        }

        sb.AppendLine("近期已压缩对话要点：");
        int remainingLines = 24;
        foreach (var group in olderGroups)
        {
            if (remainingLines <= 0) break;
            foreach (var msg in group.Messages)
            {
                if (remainingLines <= 0) break;
                var role = GetStringOrNull(msg["role"]) ?? "unknown";
                var content = GetStringOrNull(msg["content"]) ?? string.Empty;
                if (role == "user")
                {
                    sb.AppendLine($"- 用户: {TruncateSnippet(content, 200)}");
                    remainingLines--;
                }
                else if (role == "assistant")
                {
                    if (msg["tool_calls"] is JsonArray toolCalls && toolCalls.Count > 0)
                    {
                        var names = toolCalls
                            .Select(tc => tc?["function"]?["name"]?.ToString())
                            .Where(n => !string.IsNullOrWhiteSpace(n));
                        sb.AppendLine($"- 助手调用工具: {string.Join(", ", names)}");
                        remainingLines--;
                    }
                    else if (!string.IsNullOrWhiteSpace(content))
                    {
                        sb.AppendLine($"- 助手: {TruncateSnippet(content, 220)}");
                        remainingLines--;
                    }
                }
                else if (role == "tool" && !string.IsNullOrWhiteSpace(content))
                {
                    sb.AppendLine($"  - 工具结果: {TruncateSnippet(content, 160)}");
                    remainingLines--;
                }
            }
        }

        return sb.ToString().Trim();
    }

    private static string TruncateSnippet(string text, int maxChars)
    {
        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (singleLine.Length <= maxChars) return singleLine;
        return singleLine[..maxChars] + "...";
    }

    private static string TruncateMiddleText(string text, int maxTokens)
    {
        int approxMaxChars = Math.Max(400, maxTokens * 2);
        if (text.Length <= approxMaxChars) return text;

        int headChars = approxMaxChars / 2;
        int tailChars = approxMaxChars / 3;
        int omitted = text.Length - headChars - tailChars;
        if (omitted <= 0) return text;

        return $"{text[..headChars]}\n\n... [内容过长，已自动截断中间 {omitted} 个字符以适配模型上下文上限] ...\n\n{text[^tailChars..]}";
    }
}
