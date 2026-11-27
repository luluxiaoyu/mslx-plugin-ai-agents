using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using MSLX.Plugin.AiAgents.Models;
using MSLX.Plugin.AiAgents.Utils;
using MSLX.SDK;
using MSLX.SDK.Interfaces;
using MSLX.SDK.IServices;
using MSLX.SDK.Models;
using MSLX.SDK.Models.Instance;
using MSLX.SDK.Models.Tasks;

namespace MSLX.Plugin.AiAgents.Services;

public partial class AiService
{
    private readonly HttpClient _httpClient;
    private readonly IInstanceLifecycleService _lifecycleService;
    private readonly IInstanceConsoleService _consoleService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IMemoryCache _memoryCache;
    private readonly McpUploadService _uploadService;
    private readonly ServerTerminalService _terminalService;
    private readonly ILogger<AiService> _logger;

    public AiService(
        IInstanceLifecycleService lifecycleService,
        IInstanceConsoleService consoleService,
        IServiceScopeFactory scopeFactory,
        IServiceProvider serviceProvider,
        IMemoryCache memoryCache,
        McpUploadService uploadService,
        ServerTerminalService terminalService,
        ILogger<AiService> logger)
    {
        _httpClient = new HttpClient();
        _lifecycleService = lifecycleService;
        _consoleService = consoleService;
        _scopeFactory = scopeFactory;
        _serviceProvider = serviceProvider;
        _memoryCache = memoryCache;
        _uploadService = uploadService;
        _terminalService = terminalService;
        _logger = logger;
    }

    public async Task ProcessChatAsync(
        JsonArray messages,
        Func<string, Task> onChunkReceived,
        Func<string, object, Task> onToolExecuted,
        string userId, CancellationToken cancellationToken, Action<string, string, string?, string?> recordTool,
        Func<AiContextStatus, Task>? onContextStatus = null)
    {
        var configBridge = MSLX.SDK.MSLX.Config.GetPluginConfig("mslx-plugin-ai-agents");
        var config = configBridge.ReadConfig();
        string apiKey = (string?)(config["aiApiKey"]) ?? "";
        string baseUrl = (string?)(config["aiBaseUrl"]) ?? "https://api.deepseek.com/v1";
        string modelName = (string?)(config["aiModelName"]) ?? "deepseek-chat";
        var (maxContextTokens, maxContextLabel) = ModelContextLengthHelper.Resolve((string?)config["aiContextLength"]);
        var systemPrompt = AiSkillManager.GetSystemPrompt();

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("请在当前 AI 助手窗口打开模型设置，填写 API Key。");

        var requestUrl = $"{baseUrl.TrimEnd('/')}/chat/completions";

        var reqMessages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = systemPrompt
            }
        };

        int remainingInputNonSystem = 0;
        foreach (var msg in messages)
        {
            if (msg != null)
            {
                reqMessages.Add(msg.DeepClone());
                if (GetStringOrNull(msg["role"]) != "system")
                {
                    remainingInputNonSystem++;
                }
            }
        }

        var tools = GetToolsDefinition();
        var toolsTokens = GetToolsTokens();
        int? knownCurrentTokens = null;
        int totalArchivedSourceCount = 0;
        bool sentInitialContextStatus = false;

        async Task<string> RunTool(string name, string args)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? taskId = null;
            string? operationId = null;
            bool submitted = false;
            recordTool(name, "executing", null, null);
            if (cancellationToken.IsCancellationRequested)
            {
                recordTool(name, "cancelled", null, null);
                cancellationToken.ThrowIfCancellationRequested();
            }
            var result = await ExecuteToolAsync(name, args, async (tool, data) =>
            {
                var details = JsonSerializer.SerializeToElement(data);
                taskId = McpRequest.GetString(details, "backgroundTaskId") ?? taskId;
                operationId = McpRequest.GetString(details, "confirmationId") ?? operationId;
                submitted |= McpRequest.GetString(details, "status") == "started";
                if (!cancellationToken.IsCancellationRequested) await onToolExecuted(tool, data);
            }, userId: userId);
            var status = result.StartsWith("[ERROR]", StringComparison.Ordinal) ? "failed"
                : operationId != null ? "pending" : submitted ? "submitted" : "succeeded";
            recordTool(name, status, taskId, operationId);
            if (!cancellationToken.IsCancellationRequested)
                await onToolExecuted(name, new { outcome = status });
            return result;
        }

        int maxTurns = 15;
        while (maxTurns-- > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (compressed, beforeTokens, afterTokens, archivedCount, summary) =
                await EnsureContextWithinLimitAsync(
                    reqMessages,
                    remainingInputNonSystem,
                    toolsTokens,
                    maxContextTokens,
                    knownCurrentTokens,
                    requestUrl,
                    apiKey,
                    modelName,
                    cancellationToken);

            if (compressed)
            {
                remainingInputNonSystem = Math.Max(0, remainingInputNonSystem - archivedCount);
                totalArchivedSourceCount += archivedCount;
                knownCurrentTokens = afterTokens;
                if (onContextStatus != null)
                {
                    await onContextStatus(BuildContextStatus(
                        afterTokens,
                        maxContextTokens,
                        maxContextLabel,
                        isEstimated: true,
                        compressed: true,
                        beforeTokens: beforeTokens,
                        afterTokens: afterTokens,
                        summary: summary,
                        archivedSourceCount: totalArchivedSourceCount));
                }
                sentInitialContextStatus = true;
            }
            else if (!sentInitialContextStatus)
            {
                if (onContextStatus != null)
                {
                    await onContextStatus(BuildContextStatus(
                        beforeTokens,
                        maxContextTokens,
                        maxContextLabel,
                        isEstimated: true));
                }
                sentInitialContextStatus = true;
            }

            var payload = new JsonObject
            {
                ["model"] = modelName,
                ["messages"] = reqMessages.DeepClone(),
                ["tools"] = tools.DeepClone(),
                ["stream"] = true,
                ["stream_options"] = new JsonObject
                {
                    ["include_usage"] = true
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            try
            {
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"模型服务返回 HTTP {(int)response.StatusCode}，请检查模型配置。");
                }

                using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(responseStream, Encoding.UTF8);

                var assistantMessage = new JsonObject { ["role"] = "assistant" };
                var toolCallsByIndex = new Dictionary<int, JsonObject>();
                var toolCallNameBuilders = new Dictionary<int, StringBuilder>();
                var toolCallArgsBuilders = new Dictionary<int, StringBuilder>();
                var textContent = new StringBuilder();
                int streamedLength = 0;
                int? turnPromptTokens = null;
                int? turnCompletionTokens = null;
                int? turnTotalTokens = null;

                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:")) continue;

                    var data = line.Substring(5).Trim();
                    if (data == "[DONE]") break;

                    JsonObject? chunk;
                    try
                    {
                        chunk = JsonNode.Parse(data)?.AsObject();
                    }
                    catch
                    {
                        continue;
                    }

                    if (chunk?["usage"] is JsonObject usageObj)
                    {
                        if (usageObj["prompt_tokens"] is JsonValue pt && pt.TryGetValue<int>(out var ptVal))
                            turnPromptTokens = ptVal;
                        if (usageObj["completion_tokens"] is JsonValue ct && ct.TryGetValue<int>(out var ctVal))
                            turnCompletionTokens = ctVal;
                        if (usageObj["total_tokens"] is JsonValue tt && tt.TryGetValue<int>(out var ttVal))
                            turnTotalTokens = ttVal;
                    }

                    var choices = chunk?["choices"]?.AsArray();
                    if (choices == null || choices.Count == 0) continue;

                    var delta = choices[0]?["delta"]?.AsObject();
                    if (delta == null)
                    {
                        delta = choices[0]?["message"]?.AsObject();
                    }
                    if (delta == null) continue;

                    var contentDelta = GetStringOrNull(delta["content"]);
                    if (!string.IsNullOrEmpty(contentDelta))
                    {
                        textContent.Append(contentDelta);
                        string full = textContent.ToString();

                        int stopIndex = full.Length;
                        int sugIndex = full.IndexOf("<<<", StringComparison.OrdinalIgnoreCase);
                        if (sugIndex >= 0 && sugIndex < stopIndex)
                        {
                            stopIndex = sugIndex;
                        }
                        int dsmlIndex = full.IndexOf("<｜DSML｜", StringComparison.OrdinalIgnoreCase);
                        if (dsmlIndex >= 0 && dsmlIndex < stopIndex)
                        {
                            stopIndex = dsmlIndex;
                        }

                        // 若结尾刚好处于 <<< 的部分前缀（如 "<" 或 "<<"），暂缓发送末尾这 1-2 个字符，等下一个 chunk 确认
                        if (stopIndex == full.Length)
                        {
                            if (full.EndsWith("<<", StringComparison.Ordinal))
                            {
                                stopIndex -= 2;
                            }
                            else if (full.EndsWith("<", StringComparison.Ordinal))
                            {
                                stopIndex -= 1;
                            }
                        }

                        if (stopIndex > streamedLength)
                        {
                            string toSend = full.Substring(streamedLength, stopIndex - streamedLength);
                            streamedLength = stopIndex;
                            await onChunkReceived(toSend);
                        }
                    }

                    var deltaToolCalls = delta["tool_calls"]?.AsArray();
                    if (deltaToolCalls == null) continue;

                    foreach (var deltaToolCall in deltaToolCalls)
                    {
                        if (deltaToolCall is not JsonObject toolCallObj) continue;

                        int index = toolCallObj["index"] is JsonValue indexValue && indexValue.TryGetValue<int>(out int idx)
                            ? idx
                            : toolCallsByIndex.Count;

                        if (!toolCallsByIndex.TryGetValue(index, out var toolCallBuilder))
                        {
                            toolCallBuilder = new JsonObject
                            {
                                ["id"] = GetStringOrNull(toolCallObj["id"]) ?? $"call_{index}",
                                ["type"] = GetStringOrNull(toolCallObj["type"]) ?? "function",
                                ["function"] = new JsonObject()
                            };
                            toolCallsByIndex[index] = toolCallBuilder;
                            toolCallNameBuilders[index] = new StringBuilder();
                            toolCallArgsBuilders[index] = new StringBuilder();
                        }

                        var idDelta = GetStringOrNull(toolCallObj["id"]);
                        if (!string.IsNullOrEmpty(idDelta) && string.IsNullOrEmpty(GetStringOrNull(toolCallBuilder["id"])))
                        {
                            toolCallBuilder["id"] = idDelta;
                        }

                        var functionObj = toolCallObj["function"]?.AsObject();
                        if (functionObj == null) continue;

                        var nameDelta = GetStringOrNull(functionObj["name"]);
                        if (!string.IsNullOrEmpty(nameDelta))
                        {
                            toolCallNameBuilders[index].Append(nameDelta);
                        }

                        var argsDelta = GetStringOrNull(functionObj["arguments"]);
                        if (!string.IsNullOrEmpty(argsDelta))
                        {
                            toolCallArgsBuilders[index].Append(argsDelta);
                        }
                    }
                }

                if (toolCallsByIndex.Count == 0)
                {
                    string finalFull = textContent.ToString();
                    int finalStop = finalFull.Length;
                    int finalSug = finalFull.IndexOf("<<<", StringComparison.OrdinalIgnoreCase);
                    if (finalSug >= 0 && finalSug < finalStop) finalStop = finalSug;
                    int finalDsml = finalFull.IndexOf("<｜DSML｜", StringComparison.OrdinalIgnoreCase);
                    if (finalDsml >= 0 && finalDsml < finalStop) finalStop = finalDsml;
                    if (finalStop > streamedLength)
                    {
                        string remaining = finalFull.Substring(streamedLength, finalStop - streamedLength);
                        streamedLength = finalStop;
                        await onChunkReceived(remaining);
                    }
                }

                if (toolCallsByIndex.Count > 0)
                {
                    if (textContent.Length > 0)
                    {
                        assistantMessage["content"] = textContent.ToString();
                    }

                    var toolCallsArray = new JsonArray();
                    foreach (var kvp in toolCallsByIndex.OrderBy(k => k.Key))
                    {
                        var functionObj = kvp.Value["function"]!.AsObject();
                        functionObj["name"] = toolCallNameBuilders[kvp.Key].ToString();
                        functionObj["arguments"] = toolCallArgsBuilders[kvp.Key].ToString();
                        toolCallsArray.Add(kvp.Value);
                    }

                    assistantMessage["tool_calls"] = toolCallsArray;
                    reqMessages.Add(assistantMessage);

                    var addedToolMessages = new List<JsonObject>();
                    foreach (var call in toolCallsArray)
                    {
                        var toolCallId = call?["id"]?.ToString() ?? "";
                        var functionObj = call?["function"]?.AsObject();
                        var functionName = functionObj?["name"]?.ToString() ?? "";
                        var argumentsStr = functionObj?["arguments"]?.ToString() ?? "{}";

                        var toolResult = await RunTool(functionName, argumentsStr);

                        var toolMsg = new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = toolCallId,
                            ["content"] = toolResult
                        };
                        reqMessages.Add(toolMsg);
                        addedToolMessages.Add(toolMsg);
                    }

                    int deltaTokens = EstimateMessageTokens(assistantMessage) + EstimateMessagesTokens(addedToolMessages);
                    knownCurrentTokens = turnPromptTokens.HasValue
                        ? turnPromptTokens.Value + deltaTokens
                        : EstimateMessagesTokens(reqMessages) + toolsTokens + 3;

                    if (onContextStatus != null)
                    {
                        await onContextStatus(BuildContextStatus(
                            knownCurrentTokens.Value,
                            maxContextTokens,
                            maxContextLabel,
                            isEstimated: !turnPromptTokens.HasValue));
                    }

                    continue;
                }

                var contentText = textContent.ToString();

                List<string>? suggestions = null;
                var sugMatch = Regex.Match(contentText, @"<<<SUGGESTIONS:\s*(?<json>\[[\s\S]*?\])\s*>>>?", RegexOptions.Singleline);
                if (sugMatch.Success)
                {
                    try
                    {
                        suggestions = JsonSerializer.Deserialize<List<string>>(sugMatch.Groups["json"].Value);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "解析建议回复 JSON 失败");
                    }
                }
                contentText = Regex.Replace(contentText, @"<<<SUGGESTIONS:[\s\S]*?(?:>>>|$)", "", RegexOptions.Singleline).Trim();

                if (suggestions != null && suggestions.Count > 0)
                {
                    await onToolExecuted("suggested_replies", suggestions);
                }

                await TryHandleDsmlTextToolCallsAsync(contentText, RunTool);
                cancellationToken.ThrowIfCancellationRequested();

                if (onContextStatus != null)
                {
                    int finalTokens = turnTotalTokens
                        ?? (turnPromptTokens.HasValue
                            ? turnPromptTokens.Value + (turnCompletionTokens ?? EstimateTextTokens(textContent.ToString()))
                            : EstimateMessagesTokens(reqMessages) + toolsTokens + 3 + EstimateTextTokens(textContent.ToString()) + 4);

                    await onContextStatus(BuildContextStatus(
                        finalTokens,
                        maxContextTokens,
                        maxContextLabel,
                        isEstimated: !turnTotalTokens.HasValue && !turnPromptTokens.HasValue));
                }

                return;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "无法请求模型服务");
                throw new InvalidOperationException("模型服务连接失败，请检查模型地址和网络。");
            }
        }
        throw new InvalidOperationException("本轮已达到工具调用上限，请核对已执行的操作。");
    }

    private string GetJavaMajorVersion(string versionStr)
    {
        if (string.IsNullOrWhiteSpace(versionStr)) return "17";
        var match = Regex.Match(versionStr, @"(?:1\.)?(?<ver>\d+)");
        return match.Success ? match.Groups["ver"].Value : "17";
    }

    private int GetRecommendedJavaMajor(string mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion)) return 17;

        var match26 = Regex.Match(mcVersion, @"\b(2\d(?:\.\d+)*)\b");
        if (match26.Success)
        {
            if (float.TryParse(match26.Value, out float verNum) && verNum >= 26.1f)
                return 25;
        }

        var match = Regex.Match(mcVersion, @"\b(\d+(?:\.\d+)+)\b");
        string cleanVersion = match.Success ? match.Value : mcVersion.Trim();

        int CompareVersions(string v1, string v2)
        {
            var parts1 = v1.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
            var parts2 = v2.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
            int maxLen = Math.Max(parts1.Length, parts2.Length);
            for (int i = 0; i < maxLen; i++)
            {
                int p1 = i < parts1.Length ? parts1[i] : 0;
                int p2 = i < parts2.Length ? parts2[i] : 0;
                if (p1 != p2) return p1 - p2;
            }
            return 0;
        }

        if (CompareVersions(cleanVersion, "26.1") >= 0) return 25;
        if (CompareVersions(cleanVersion, "1.20.5") >= 0) return 21;
        if (CompareVersions(cleanVersion, "1.18") >= 0) return 17;
        if (CompareVersions(cleanVersion, "1.17") >= 0) return 16;
        return 8;
    }

    private JsonNode? ConvertJTokenToJsonNode(object? data)
    {
        if (data is JToken token)
        {
            try
            {
                return JsonNode.Parse(token.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private async Task<(bool Success, JToken? Data, string? Msg)> GetMslApiDataAsync(string path)
    {
        try
        {
            string url = $"https://api.mslmc.cn/v4{path}";
            var resp = await MSLX.SDK.MSLX.Http.GetAsync(url);
            if (resp.IsSuccessStatusCode && !string.IsNullOrEmpty(resp.Content))
            {
                var json = JObject.Parse(resp.Content);
                if (json["code"]?.ToString() == "200")
                {
                    return (true, json["data"], json["message"]?.ToString());
                }
                return (false, null, json["message"]?.ToString() ?? "API 返回非200状态");
            }

            using var httpResp = await _httpClient.GetAsync(url);
            if (httpResp.IsSuccessStatusCode)
            {
                var content = await httpResp.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);
                if (json["code"]?.ToString() == "200")
                {
                    return (true, json["data"], json["message"]?.ToString());
                }
                return (false, null, json["message"]?.ToString() ?? "API 返回非200状态");
            }
            return (false, null, $"HTTP 请求失败: {resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private async Task<string?> TryHandleDsmlTextToolCallsAsync(string contentText, Func<string, string, Task<string>> runTool)
    {
        if (string.IsNullOrWhiteSpace(contentText) || !contentText.Contains("<｜DSML｜invoke"))
        {
            return null;
        }

        try
        {
            var invokeMatch = Regex.Match(contentText, @"<｜DSML｜invoke\s+name=""(?<name>[^""]+)""[^>]*>(?<body[\s\S]*?)</｜DSML｜invoke>");
            if (invokeMatch.Success)
            {
                string rawName = invokeMatch.Groups["name"].Value;
                string body = invokeMatch.Groups["body"].Value;

                string toolName = rawName switch
                {
                    "create_server" => "create_mc_server",
                    "create_mc_server" => "create_mc_server",
                    "query_cores" => "query_available_server_cores",
                    "query_java" => "query_java_environments",
                    "install_java" => "install_online_java",
                    "install_online_java" => "install_online_java",
                    "check_status" => "query_creation_status",
                    "update_instance" => "update_instance_settings",
                    _ => rawName
                };

                var argsObj = new JsonObject();
                var paramMatches = Regex.Matches(body, @"<｜DSML｜parameter\s+name=""(?<pname>[^""]+)""[^>]*>(?<pval>[^<]*)</｜DSML｜parameter>");
                foreach (Match pm in paramMatches)
                {
                    string pname = pm.Groups["pname"].Value;
                    string pval = pm.Groups["pval"].Value;

                    string mappedPName = pname switch
                    {
                        "core_name" => "server_type",
                        "version" => "mc_version",
                        "name" => "server_name",
                        _ => pname
                    };

                    argsObj[mappedPName] = pval;
                }

                string toolResult = await runTool(toolName, argsObj.ToJsonString());

                string cleanText = Regex.Replace(contentText, @"<｜DSML｜tool_calls[\s\S]*?</｜DSML｜tool_calls>", "").Trim();
                return string.IsNullOrWhiteSpace(cleanText) ? toolResult : $"{cleanText}\n\n{toolResult}";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "解析 DSML 文本标签失败");
        }

        return null;
    }

    internal Task<string> ExecuteMcpToolAsync(string name, string argsJson, string tokenId)
    {
        return ExecuteToolAsync(name, argsJson, (_, _) => Task.CompletedTask, userId: $"mcp:{tokenId}");
    }

    public List<McpToolDefinition> GetMcpToolDefinitions()
    {
        var list = new List<McpToolDefinition>();
        var tools = GetToolsDefinition();
        foreach (var node in tools)
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue("function", out var fnNode) && fnNode is JsonObject fn)
            {
                var name = fn["name"]?.ToString() ?? string.Empty;
                var desc = fn["description"]?.ToString() ?? string.Empty;
                var schema = fn["parameters"] as JsonObject ?? new JsonObject { ["type"] = "object" };
                if (schema["properties"] is JsonObject properties)
                {
                    foreach (var idName in McpToolDefinition.TargetIdFields)
                    {
                        if (properties[idName] is JsonObject id)
                        {
                            id["type"] = "integer";
                            id["minimum"] = 1;
                            id["maximum"] = int.MaxValue;
                        }
                    }
                }

                list.Add(new McpToolDefinition
                {
                    Name = name,
                    Description = desc,
                    InputSchema = JsonSerializer.SerializeToElement(schema)
                });
            }
        }
        list.Add(new McpToolDefinition
        {
            Name = "query_operation_status",
            Description = "查询当前访问令牌提交的待审批操作或执行结果。通过原工具返回的操作编号查询。",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { operation_id = new { type = "string" } },
                required = new[] { "operation_id" }
            })
        });
        return list;
    }

    private JsonArray GetToolsDefinition()
    {
        var tools = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "update_instance_settings",
                    ["description"] = "修改 MSLX 服务器基础实例属性（如切换绑定的 Java 环境，调整最大/最小内存 MB，修改运行核心 Core 文件名，附加 JVM 参数等系统级配置）。Java 参数传入 'suitable' 或 'auto' 时系统会自动根据规则精准推导（NeoForge 26.x / MC 26.1+ 强制绑定 Java 25；1.20.5~1.21 绑定 Java 21；1.18~1.20.4 绑定 Java 17；1.17 绑定 Java 16；1.13及更低绑定 Java 8）。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                            ["java"] = new JsonObject { ["type"] = "string", ["description"] = "Java 环境标识，如 'MSLX://Java/25'、'suitable' (自动精准推导)、'MSLX://Java/21'、'MSLX://Java/17'、'MSLX://Java/8'" },
                            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "服务器实例名称" },
                            ["max_m"] = new JsonObject { ["type"] = "integer", ["description"] = "最大内存 (MB)，如 4096" },
                            ["min_m"] = new JsonObject { ["type"] = "integer", ["description"] = "最小内存 (MB)，如 1024" },
                            ["core"] = new JsonObject { ["type"] = "string", ["description"] = "核心 JAR 文件名，如 'neoforge-26.2.jar'" },
                            ["core_file_key"] = new JsonObject { ["type"] = "string", ["description"] = "可选：已通过 /mcp/uploads 或 stage_upload_file 上传的新核心文件 Key，用于替换服务端核心" },
                            ["core_local_path"] = new JsonObject { ["type"] = "string", ["description"] = "可选：宿主机本地新核心文件的绝对路径，用于替换服务端核心" },
                            ["core_content_base64"] = new JsonObject { ["type"] = "string", ["description"] = "可选：Base64 编码的新核心文件内容，用于替换服务端核心" },
                            ["args"] = new JsonObject { ["type"] = "string", ["description"] = "附加 JVM 启动参数" },
                            ["auto_restart"] = new JsonObject { ["type"] = "boolean", ["description"] = "崩溃时是否自动重启" }
                        }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "list_server_files",
                    ["description"] = "列出指定服务器实例目录或子目录下的所有文件和文件夹列表。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                            ["dir_path"] = new JsonObject { ["type"] = "string", ["description"] = "相对目录路径，如 '' (根目录), 'plugins', 'mods', 'logs'" }
                        }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "read_server_file",
                    ["description"] = "读取指定服务器实例目录下的任意文本文件（如 server.properties, eula.txt, spigot.yml, paper-global.yml, 插件/模组配置文件及运行日志等）。底层采用 FileShare.ReadWrite 共享模式，即使服务器正在运行中也可安全无冲突读取进行日志排查。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                            ["file_path"] = new JsonObject { ["type"] = "string", ["description"] = "相对文件路径，如 'server.properties', 'logs/latest.log', 'logs/debug.log', 'plugins/Essentials/config.yml'" }
                        },
                        ["required"] = new JsonArray { "file_path" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "write_server_file",
                    ["description"] = "创建或覆盖写入指定服务器实例目录下的文本文件（如修改 server.properties, eula.txt, 插件配置）。若目标文件已存在，系统会自动挂起操作并在前端弹出授权确认卡片，用户点击确认后才会真正写入，无需在参数中指定确认状态。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                            ["file_path"] = new JsonObject { ["type"] = "string", ["description"] = "相对文件路径，如 'server.properties', 'eula.txt', 'spigot.yml'" },
                            ["content"] = new JsonObject { ["type"] = "string", ["description"] = "写入文件的完整文本内容" }
                        },
                        ["required"] = new JsonArray { "file_path", "content" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "delete_server_file",
                    ["description"] = "删除指定服务器实例目录下的文件或文件夹（如删除存档 'world'、旧插件、模组或日志文件）。【请直接果断调用本工具，严禁在回复文本中反问用户；系统会自动挂起操作并在前端弹出确认卡片，用户点击确认授权后才会真正删除】。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                            ["file_path"] = new JsonObject { ["type"] = "string", ["description"] = "相对文件或目录路径，如 'world', 'plugins/old_plugin.jar', 'mods/test.jar'" }
                        },
                        ["required"] = new JsonArray { "file_path" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "query_creation_status",
                    ["description"] = "查询指定服务器实例后台建服与安装的最新进度、状态消息及是否成功/失败。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID" }
                        },
                        ["required"] = new JsonArray { "server_id" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "query_java_environments",
                    ["description"] = "扫描并查询当前主机上已安装的所有 Java 环境列表（如 Java 8, 16, 17, 21, 25 等及对应的 MSLX 路径标识）。支持指定 force_rescan 强制全盘重新扫描，可直接发现宿主机新安装的 JDK。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["force_rescan"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否强制全盘重新扫描（忽略本地缓存），默认为 true，推荐在排查 Java 版本不匹配或未找到所需版本时开启" }
                        }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "install_online_java",
                    ["description"] = "调用 MSLX 官方内置镜像源，在后台一键下载、校验并部署指定版本的 Java 运行环境（如 Java 8, 11, 16, 17, 21, 25 等）。安装完成后会自动全盘刷新环境，使其即刻作为 MSLX://Java/{version} 生效。严禁要求用户在宿主机终端手动运行 brew/apt 安装，缺少 Java 时直接果断调用本工具！",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["version"] = new JsonObject { ["type"] = "integer", ["description"] = "需要安装的 Java 主要版本，如 8, 17, 21, 25" }
                        },
                        ["required"] = new JsonArray { "version" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "query_available_server_cores",
                    ["description"] = "向 MSL 镜像站 API 实时动态获取当前支持的所有服务端核心类型与游戏版本。本工具 100% 动态读取 API，绝无任何本地静态写死：\n1. 留空参数：实时获取 API 提供的全部核心分类与核心类型列表；\n2. 传入 version 或 search（如 '26.4', '1.20.2', 'snapshot'）：自动向 API 动态拉取全部核心类型并实时并行检索哪些核心支持该版本；\n3. 传入 core_name（如 'vanilla-snapshot', 'paper', 'fabric'）：获取指定核心的全部支持版本列表。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["core_name"] = new JsonObject { ["type"] = "string", ["description"] = "可选：指定要查询的服务端核心名称（如 'vanilla-snapshot', 'paper', 'fabric' 等）。" },
                            ["version"] = new JsonObject { ["type"] = "string", ["description"] = "可选：要搜索匹配的游戏版本号或关键字（如 '26.4', '1.20.2', 'snapshot'）。工具会自动向 API 动态拉取全部核心进行全量实时检索匹配！" }
                        }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "create_mc_server",
                    ["description"] = "在 MSLX 面板中创建并部署 Minecraft 服务器实例。支持 4 种开服模式：1. 快速在线部署模式 (Paper/NeoForge/Fabric/Forge/Vanilla/Vanilla-Snapshot/Spigot/Mohist 等)；2. 整合包/压缩包部署模式 (支持本地 .zip 绝对路径或远程 URL 下载)；3. MCDReforged (MCDR) 架构模式；4. 自定义/导入模式 (自定义主机路径、核心与 JVM 参数)。系统会根据 MC 版本自动精确推导并绑定最匹配的 Java 版本。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_name"] = new JsonObject { ["type"] = "string", ["description"] = "服务器实例名称，如 '我的 Fabric 1.20.2 服务器'" },
                            ["server_type"] = new JsonObject { ["type"] = "string", ["description"] = "核心类型，如 paper, fabric, neoforge, forge, spigot, vanilla, vanilla-snapshot (原版快照版/预览版), mohist, arclight, custom, package, mcdr" },
                            ["mc_version"] = new JsonObject { ["type"] = "string", ["description"] = "Minecraft 游戏版本号或核心版本号，如 '1.20.2', '26.2', '1.12.2'" },
                            ["java"] = new JsonObject { ["type"] = "string", ["description"] = "可选 Java 环境标识（如 'MSLX://Java/21'），留空时系统按 MC 版本自动推导并绑定" },
                            ["max_m"] = new JsonObject { ["type"] = "integer", ["description"] = "最大分配内存 MB，默认 2048" },
                            ["min_m"] = new JsonObject { ["type"] = "integer", ["description"] = "最小分配内存 MB，默认 1024" },
                            ["args"] = new JsonObject { ["type"] = "string", ["description"] = "附加 JVM 启动参数" },
                            ["ignore_eula"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否自动同意 EULA，默认 true" },
                            ["server_port"] = new JsonObject { ["type"] = "string", ["description"] = "端口号，默认 '25565'" },
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "【自定义目录】服务器存放的绝对路径" },
                            ["package_local_path"] = new JsonObject { ["type"] = "string", ["description"] = "【整合包模式】宿主机上的整合包 .zip/.mrpack 文件的绝对路径" },
                            ["package_url"] = new JsonObject { ["type"] = "string", ["description"] = "【整合包模式】整合包 .zip/.mrpack 的远程下载地址 (http/https)" },
                            ["package_file_key"] = new JsonObject { ["type"] = "string", ["description"] = "【整合包模式】已通过 /mcp/uploads 或 stage_upload_file 上传的整合包 .zip/.mrpack 文件 Key (upload_id)" },
                            ["mcdr"] = new JsonObject { ["type"] = "boolean", ["description"] = "【MCDR 模式】是否启用 MCDReforged 架构控制端" },
                            ["mcdr_python"] = new JsonObject { ["type"] = "string", ["description"] = "【MCDR 模式】Python 可执行文件路径，默认为 'python'" },
                            ["mcdr_handler"] = new JsonObject { ["type"] = "string", ["description"] = "【MCDR 模式】Handler 类型，如 'vanilla', 'paper', 'fabric', 'custom'" },
                            ["mcdr_install"] = new JsonObject { ["type"] = "boolean", ["description"] = "【MCDR 模式】是否自动 pip 安装 MCDReforged，默认 true" },
                            ["mcdr_pip_mirror"] = new JsonObject { ["type"] = "string", ["description"] = "【MCDR 模式】pip 镜像源 URL" },
                            ["core_url"] = new JsonObject { ["type"] = "string", ["description"] = "【自定义核心】核心 JAR 文件的直接下载链接" },
                            ["core_file_key"] = new JsonObject { ["type"] = "string", ["description"] = "【上传核心】已通过 /mcp/uploads 或 stage_upload_file 上传的核心 JAR 文件 Key (upload_id)" },
                            ["core_local_path"] = new JsonObject { ["type"] = "string", ["description"] = "【本地核心】宿主机本地核心 JAR 文件的绝对路径" },
                            ["core_filename"] = new JsonObject { ["type"] = "string", ["description"] = "【自定义核心】核心 JAR 文件名，如 'server.jar'" }
                        }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "update_server_config",
                    ["description"] = "更新服务器的核心属性（如修改 server.properties 中的端口 server_port、正版验证 online_mode、PVP pvp、最大玩家数 max_players、MOTD motd）。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                            ["server_port"] = new JsonObject { ["type"] = "integer", ["description"] = "服务器通信端口，如 18889" },
                            ["online_mode"] = new JsonObject { ["type"] = "boolean", ["description"] = "正版验证设置" },
                            ["pvp"] = new JsonObject { ["type"] = "boolean", ["description"] = "PVP 设置" },
                            ["max_players"] = new JsonObject { ["type"] = "integer", ["description"] = "最大玩家数" },
                            ["motd"] = new JsonObject { ["type"] = "string", ["description"] = "服务器标语 (MOTD)" }
                        }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "control_server",
                    ["description"] = "控制服务器的启动、停止或重启。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                            ["action"] = new JsonObject { ["type"] = "string", ["description"] = "动作，取值为 start, stop, restart" }
                        },
                        ["required"] = new JsonArray { "action" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "query_server_status",
                    ["description"] = "查询当前所有服务器或指定服务器的运行状态列表。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "可选服务器 ID" }
                        }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "read_server_log",
                    ["description"] = "专用于诊断服务器崩溃与实时日志排查。读取服务器 `logs/latest.log` 末尾指定行数的文本，或直接检索 `crash-reports/` 目录下最新的崩溃报告文件。采用 FileShare.ReadWrite 共享模式，即使服务器运行中或崩溃卡死时也可安全读取。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                            ["log_type"] = new JsonObject { ["type"] = "string", ["description"] = "日志类型，取值为 'latest' (运行日志) 或 'crash_report' (崩溃文件)，默认为 'latest'" },
                            ["tail_lines"] = new JsonObject { ["type"] = "integer", ["description"] = "读取末尾行数，默认 100 行" }
                        }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "copy_server_file",
                    ["description"] = "在指定服务器实例目录下复制文件或文件夹（如备份配置文件 `server.properties` 为 `server.properties.bak`，备份存档，或复制 Mod/插件）。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                            ["source_path"] = new JsonObject { ["type"] = "string", ["description"] = "源相对路径，如 'server.properties', 'mods/test.jar', 'world'" },
                            ["destination_path"] = new JsonObject { ["type"] = "string", ["description"] = "目标相对路径，如 'server.properties.bak', 'mods_bak/test.jar', 'world_backup'" }
                        },
                        ["required"] = new JsonArray { "source_path", "destination_path" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "move_server_file",
                    ["description"] = "在指定服务器实例目录下移动或重命名文件/文件夹（如将崩服的 Mod `mod.jar` 重命名为 `mod.jar.disabled` 以禁用模组，移动备份，重命名地图文件夹等）。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                            ["source_path"] = new JsonObject { ["type"] = "string", ["description"] = "源相对路径，如 'mods/badmod.jar', 'server.properties'" },
                            ["destination_path"] = new JsonObject { ["type"] = "string", ["description"] = "目标相对路径，如 'mods/badmod.jar.disabled', 'server.properties.old'" }
                        },
                        ["required"] = new JsonArray { "source_path", "destination_path" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "query_system_metrics",
                    ["description"] = "实时查询宿主机及指定 Minecraft 服务器实例的 CPU 使用率、已用/可用系统内存、磁盘空间、网卡流量以及运行实例性能状态。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "可选服务器 ID" }
                        }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "list_server_mods_plugins",
                    ["description"] = "专门列出指定服务器实例目录下的 `mods/` 或 `plugins/` 文件夹中的所有模组/插件文件（包括启用状态 `.jar` 和已禁用 `.jar.disabled`）、文件大小及修改时间。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                            ["folder_type"] = new JsonObject { ["type"] = "string", ["description"] = "分类，取值为 'mods', 'plugins', 'both'，默认为 'both'" }
                        }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "send_server_command",
                    ["description"] = "向正在运行的 Minecraft 服务器控制台发送一条指令（如 'say Hello', 'op player', 'whitelist add player' 等）。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "控制台指令内容" }
                        },
                        ["required"] = new JsonArray { "command" }
                    }
                }
            }
        };

        RegisterOpsTools(tools);
        RegisterBackupFrpTools(tools);
        RegisterResourcesTools(tools);
        RegisterUploadTools(tools);
        RegisterTerminalTools(tools);

        return tools;
    }

    private async Task<string> ExecuteToolAsync(string name, string argsJson, Func<string, object, Task> onToolExecuted,
        bool assumeConfirmed = false, string userId = "")
    {
        try
        {
            try
            {
                await onToolExecuted(name, new { status = "executing", tool = name });
            }
            catch { }

            var args = JsonNode.Parse(argsJson)?.AsObject();

            switch (name)
            {
                case "update_instance_settings":
                {
                    int targetId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                    {
                        int.TryParse(args["server_id"].ToString(), out targetId);
                    }

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    var targetServer = targetId > 0
                        ? serverList.FirstOrDefault(s => s.ID == targetId)
                        : serverList.OrderByDescending(s => s.ID).FirstOrDefault();

                    if (targetServer == null)
                    {
                        return "[ERROR] 未找到目标服务器实例。";
                    }

                    if (args != null && args.ContainsKey("java") && args["java"] != null)
                    {
                        string rawJava = args["java"].ToString().Trim();

                        if (rawJava.Equals("suitable", StringComparison.OrdinalIgnoreCase) ||
                            rawJava.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                            rawJava.Contains("合适") || rawJava.Contains("推荐"))
                        {
                            string versionHint = !string.IsNullOrEmpty(targetServer.Name) ? targetServer.Name : targetServer.Core;
                            int recMajor = GetRecommendedJavaMajor(versionHint);
                            rawJava = $"MSLX://Java/{recMajor}";
                        }

                        int targetMajor = 0;
                        if (rawJava.StartsWith("MSLX://Java/", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(rawJava.Replace("MSLX://Java/", "", StringComparison.OrdinalIgnoreCase), out targetMajor);
                        }
                        else if (!File.Exists(rawJava))
                        {
                            var match = Regex.Match(rawJava, @"\d+");
                            if (match.Success && int.TryParse(match.Value, out int m))
                            {
                                targetMajor = m;
                                rawJava = $"MSLX://Java/{targetMajor}";
                            }
                        }

                        if (targetMajor > 0)
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var javaScanner = scope.ServiceProvider.GetRequiredService<IJavaScannerService>();
                            // 强制全盘重新扫描，优先检测本地是否存在该版本 JDK
                            var localList = await javaScanner.ScanJavaAsync(true);
                            var localMatch = localList?.FirstOrDefault(j => {
                                return int.TryParse(GetJavaMajorVersion(j.Version), out int m) && m == targetMajor;
                            });

                            if (localMatch != null && File.Exists(localMatch.Path))
                            {
                                rawJava = localMatch.Path;
                                _logger.LogInformation("update_instance_settings: 命中本地已安装 Java {Major}: {Path}", targetMajor, localMatch.Path);
                            }
                            else
                            {
                                _logger.LogInformation("update_instance_settings: 本地未找到 Java {Major}，触发在线部署...", targetMajor);
                                await EnsureOnlineJavaAsync(targetMajor.ToString(), async (stepMsg, prog) =>
                                {
                                    await onToolExecuted("update_instance_settings", new { serverId = targetServer.ID, status = $"正在部署 Java {targetMajor}: {stepMsg}" });
                                });
                                rawJava = $"MSLX://Java/{targetMajor}";
                            }
                        }

                        targetServer.Java = rawJava;
                    }

                    if (args != null && args.ContainsKey("name") && args["name"] != null)
                    {
                        targetServer.Name = args["name"].ToString();
                    }
                    if (args != null && args.ContainsKey("max_m") && args["max_m"] != null)
                    {
                        if (int.TryParse(args["max_m"].ToString(), out int maxM))
                        {
                            targetServer.MaxM = maxM;
                        }
                    }
                    if (args != null && args.ContainsKey("min_m") && args["min_m"] != null)
                    {
                        if (int.TryParse(args["min_m"].ToString(), out int minM))
                        {
                            targetServer.MinM = minM;
                        }
                    }
                    if (args != null && args.ContainsKey("core") && args["core"] != null)
                    {
                        targetServer.Core = args["core"].ToString();
                    }
                    if (args != null && args.ContainsKey("args") && args["args"] != null)
                    {
                        targetServer.Args = args["args"].ToString();
                    }
                    if (args != null && args.ContainsKey("auto_restart") && args["auto_restart"] != null)
                    {
                        targetServer.AutoRestart = args["auto_restart"].GetValue<bool>();
                    }

                    bool hasCoreUpload = args != null && (
                        !string.IsNullOrWhiteSpace(args["core_file_key"]?.ToString()) ||
                        !string.IsNullOrWhiteSpace(args["core_local_path"]?.ToString()) ||
                        !string.IsNullOrEmpty(args["core_content_base64"]?.ToString()));

                    if (hasCoreUpload)
                    {
                        if (_lifecycleService.IsServerRunning((uint)targetServer.ID))
                        {
                            return "[ERROR] 涉及替换服务端核心文件，请先停止服务器再操作！";
                        }

                        var coreFallbackName = !string.IsNullOrWhiteSpace(targetServer.Core) ? Path.GetFileName(targetServer.Core) : "server.jar";
                        var (coreOk, coreUploadId, stagedCoreName, _, cleanupCore, coreSourceErr) = await ResolveUploadSourceAsync(
                            args,
                            userId,
                            allowTextContent: false,
                            trustExistingUploadOwnership: assumeConfirmed,
                            fallbackFileName: coreFallbackName,
                            uploadIdField: "core_file_key",
                            base64Field: "core_content_base64",
                            localPathField: "core_local_path");

                        if (!coreOk || coreUploadId == null)
                        {
                            return coreSourceErr ?? "[ERROR] 无效的核心文件来源。";
                        }

                        string serverDir = GetServerDirectory(targetServer);
                        Directory.CreateDirectory(serverDir);

                        string desiredCoreName = args?["core"]?.ToString()?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(desiredCoreName))
                        {
                            desiredCoreName = !string.IsNullOrWhiteSpace(stagedCoreName)
                                ? stagedCoreName
                                : (!string.IsNullOrWhiteSpace(targetServer.Core) ? targetServer.Core : "server.jar");
                        }

                        string destCorePath = Path.GetFullPath(Path.Combine(serverDir, desiredCoreName));
                        if (!IsWithinServerDirectory(serverDir, destCorePath) ||
                            Path.GetRelativePath(serverDir, destCorePath) == ".")
                        {
                            if (cleanupCore) _uploadService.DeleteStagedUpload(coreUploadId, userId);
                            return "[ERROR] 越权操作：核心文件路径不能超出服务器根目录。";
                        }

                        string relCorePath = Path.GetRelativePath(serverDir, destCorePath).Replace('\\', '/');
                        if (!assumeConfirmed && File.Exists(destCorePath))
                        {
                            var approvalArgs = args != null ? (JsonObject)args.DeepClone() : new JsonObject();
                            approvalArgs["server_id"] = targetServer.ID;
                            approvalArgs["core"] = relCorePath;
                            approvalArgs["core_file_key"] = coreUploadId;
                            approvalArgs.Remove("core_local_path");
                            approvalArgs.Remove("core_content_base64");
                            return await RequestReplacementAsync("update_instance_settings", approvalArgs.ToJsonString(),
                                targetServer.ID, relCorePath, userId, onToolExecuted);
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(destCorePath)!);
                        File.Move(McpUploadService.GetStagedFilePath(coreUploadId), destCorePath, overwrite: assumeConfirmed);
                        _uploadService.RemoveSession(coreUploadId);
                        targetServer.Core = relCorePath;
                    }

                    MSLX.SDK.MSLX.Config.Servers.UpdateServer(targetServer);

                    var resData = new
                    {
                        serverId = targetServer.ID,
                        name = targetServer.Name,
                        java = targetServer.Java,
                        maxM = targetServer.MaxM,
                        minM = targetServer.MinM,
                        core = targetServer.Core
                    };
                    await onToolExecuted("update_instance_settings", resData);
                    return $"[SUCCESS] 服务器 (ID: {targetServer.ID}, 名称: {targetServer.Name}) 的 MSLX 实例设置更新成功！绑定的 Java 已根据 MC 规则准确切换为 `{targetServer.Java}`，分配内存: {targetServer.MinM}M ~ {targetServer.MaxM}M！";
                }

                case "list_server_files":
                {
                    int targetId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                    {
                        int.TryParse(args["server_id"].ToString(), out targetId);
                    }
                    string dirPath = args?["dir_path"]?.ToString() ?? "";

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    var targetServer = targetId > 0
                        ? serverList.FirstOrDefault(s => s.ID == targetId)
                        : serverList.OrderByDescending(s => s.ID).FirstOrDefault();

                    if (targetServer == null)
                    {
                        return "[ERROR] 未找到目标服务器实例。";
                    }

                    string serverDir = targetServer.Base;
                    if (string.IsNullOrEmpty(serverDir) || !Directory.Exists(serverDir))
                    {
                        serverDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Servers", targetServer.ID.ToString());
                    }

                    string safeBaseDir = Path.GetFullPath(serverDir);
                    string targetDirPath = Path.GetFullPath(Path.Combine(safeBaseDir, dirPath));

                    if (!IsWithinServerDirectory(safeBaseDir, targetDirPath))
                    {
                        return "[ERROR] 越权访问：不允许查看服务器实例目录之外的文件。";
                    }

                    if (!Directory.Exists(targetDirPath))
                    {
                        return $"[ERROR] 目录不存在: {dirPath}";
                    }

                    var entries = Directory.GetFileSystemEntries(targetDirPath)
                        .Select(p => new
                        {
                            name = Path.GetFileName(p),
                            isDir = Directory.Exists(p),
                            size = Directory.Exists(p) ? 0 : new FileInfo(p).Length,
                            lastModified = File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm:ss")
                        }).ToList();

                    var listResult = new { serverId = targetServer.ID, dirPath = dirPath, count = entries.Count, items = entries };
                    await onToolExecuted("list_server_files", listResult);
                    return $"[SUCCESS] 目录 `{dirPath}` 文件列表:\n" + JsonSerializer.Serialize(entries);
                }

                case "query_creation_status":
                {
                    int targetId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                    {
                        int.TryParse(args["server_id"].ToString(), out targetId);
                    }

                    if (targetId == 0) return "[ERROR] 请提供有效的 server_id";

                    if (_memoryCache.TryGetValue(targetId.ToString(), out CacheableStatus? status) && status != null)
                    {
                        bool isSuccess = status.Progress >= 100 || status.Message.Contains("完成");
                        bool isFailure = status.Progress < 0 || status.Message.Contains("失败") || status.Message.Contains("中断") || status.Message.Contains("错误");

                        await onToolExecuted("query_creation_status", status);

                        if (isFailure)
                        {
                            return $"[ERROR] 服务器 {targetId} 安装部署失败: {status.Message}";
                        }
                        else if (isSuccess)
                        {
                            return $"[SUCCESS] 服务器 {targetId} 建服全流程已成功完成！目前可以安全进行配置文件修改和启动控制。";
                        }
                        else
                        {
                            return $"[IN_PROGRESS] 服务器 {targetId} 正在后台安装中 (进度: {status.Progress:F1}%, 状态: {status.Message})。";
                        }
                    }

                    return $"[INFO] 未找到服务器 {targetId} 的实时安装缓存，可能已安装完成或实例不存在。";
                }

                case "query_java_environments":
                {
                    bool forceRescan = true; // 默认采用全盘真实扫描，杜绝陈旧缓存误导
                    if (args != null && args.ContainsKey("force_rescan") && args["force_rescan"] != null)
                    {
                        bool.TryParse(args["force_rescan"].ToString(), out forceRescan);
                    }

                    using var scope = _scopeFactory.CreateScope();
                    var javaScanner = scope.ServiceProvider.GetRequiredService<IJavaScannerService>();
                    var list = await javaScanner.ScanJavaAsync(forceRescan);

                    var resList = list.Select(j => {
                        string major = GetJavaMajorVersion(j.Version);
                        return new
                        {
                            version = j.Version,
                            majorVersion = major,
                            vendor = j.Vendor,
                            path = j.Path,
                            mslxIdentifier = $"MSLX://Java/{major}"
                        };
                    }).ToList();

                    await onToolExecuted("query_java_environments", resList);
                    return $"[SUCCESS] 扫描到的 Java 环境列表 (已强制全盘刷新={forceRescan}):\n" +
                           JsonSerializer.Serialize(resList) +
                           "\n\n【MSLX 运行准则】若上述列表未包含所需版本，MSLX 支持直接调用 `install_online_java` 在线安装 Java 8/17/21/25，或直接在 `update_instance_settings` 中配置 `MSLX://Java/{version}`，严禁要求用户手动在终端执行 brew/apt 安装！";
                }

                case "install_online_java":
                {
                    int javaVer = 25;
                    if (args != null && args.ContainsKey("version") && args["version"] != null)
                    {
                        int.TryParse(args["version"].ToString(), out javaVer);
                    }
                    if (javaVer <= 0) javaVer = 25;

                    await onToolExecuted("install_online_java", new { version = javaVer, status = "正在从 MSLX 镜像站下载与部署..." });

                    var (success, msg) = await EnsureOnlineJavaAsync(javaVer.ToString(), async (stepMsg, prog) =>
                    {
                        await onToolExecuted("install_online_java", new { version = javaVer, status = stepMsg, progress = prog });
                    });

                    var resultData = new { version = javaVer, success = success, message = msg };
                    await onToolExecuted("install_online_java", resultData);

                    if (success)
                    {
                        return $"[SUCCESS] {msg}\n已自动全盘刷新 Java 环境！可直接调用 `update_instance_settings` 将服务器实例绑定到 `MSLX://Java/{javaVer}` 并启动！";
                    }
                    else
                    {
                        return $"[ERROR] 在线安装 Java {javaVer} 失败: {msg}";
                    }
                }

                case "query_available_server_cores":
                {
                    string? coreName = args?["core_name"]?.ToString()?.Trim()?.ToLower();
                    string? searchVersion = args?["version"]?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(searchVersion))
                    {
                        searchVersion = args?["search"]?.ToString()?.Trim();
                    }

                    if (coreName == "snapshot" || coreName == "vanilla_snapshot" || coreName == "vanillasnapshot")
                    {
                        coreName = "vanilla-snapshot";
                    }

                    // 1. 若传入了目标版本或检索词：全动态遍历当前 API 提供的全部核心，实时匹配返回（绝无任何本地写死）
                    if (!string.IsNullOrEmpty(searchVersion))
                    {
                        var (succMirrors, mirrorsData, mirrorsMsg) = await GetMslApiDataAsync("/mirrors");
                        if (!succMirrors || mirrorsData is not JObject mirrorsObj)
                        {
                            return $"[ERROR] 实时查询镜像站核心分类失败: {mirrorsMsg}";
                        }

                        var dynamicCores = new List<string>();
                        foreach (var prop in mirrorsObj.Properties())
                        {
                            if (prop.Value is JArray arr)
                            {
                                foreach (var item in arr)
                                {
                                    string cName = item.ToString().Trim();
                                    if (!string.IsNullOrEmpty(cName) && !dynamicCores.Contains(cName, StringComparer.OrdinalIgnoreCase))
                                    {
                                        dynamicCores.Add(cName);
                                    }
                                }
                            }
                        }

                        var matchedCores = new Dictionary<string, List<string>>();
                        var checkTasks = dynamicCores.Select(async c =>
                        {
                            try
                            {
                                var (vSucc, vData, _) = await GetMslApiDataAsync($"/mirrors/{c}");
                                if (vSucc)
                                {
                                    JArray? arr = vData as JArray;
                                    JObject? dObj = (arr != null && arr.Count > 0) ? arr[0] as JObject : vData as JObject;
                                    if (dObj != null && dObj.ContainsKey("versions") && dObj["versions"] is JArray vArr)
                                    {
                                        var matches = vArr
                                            .Select(x => x.ToString())
                                            .Where(v => v.Contains(searchVersion, StringComparison.OrdinalIgnoreCase))
                                            .ToList();
                                        if (matches.Count > 0)
                                        {
                                            lock (matchedCores)
                                            {
                                                matchedCores[c] = matches;
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        });
                        await Task.WhenAll(checkTasks);

                        var resultPayload = new
                        {
                            search = searchVersion,
                            total_cores_scanned = dynamicCores.Count,
                            matches = matchedCores
                        };
                        await onToolExecuted("query_available_server_cores", resultPayload);

                        if (matchedCores.Count > 0)
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine($"[SUCCESS] 经向 MSL 镜像站 API 实时全量动态检索（扫描了 API 提供的全部 {dynamicCores.Count} 种核心类型），找到以下支持 '{searchVersion}' 的核心：");
                            foreach (var kv in matchedCores)
                            {
                                sb.AppendLine($"- 核心 [{kv.Key}]：匹配到版本 [{string.Join(", ", kv.Value.Take(10))}{(kv.Value.Count > 10 ? "..." : "")}]");
                            }
                            return sb.ToString();
                        }
                        else
                        {
                            return $"[INFO] 经向 MSL 镜像站 API 实时全量检索（共扫描 API 当前提供的全部 {dynamicCores.Count} 种核心类型: {string.Join(", ", dynamicCores)}），目前均未收录包含 '{searchVersion}' 的版本。";
                        }
                    }

                    // 2. 指定了单个核心名称
                    if (!string.IsNullOrEmpty(coreName))
                    {
                        var (succ, data, msg) = await GetMslApiDataAsync($"/mirrors/{coreName}");
                        var cleanData = ConvertJTokenToJsonNode(data);
                        await onToolExecuted("query_available_server_cores", new { query = coreName, success = succ, data = cleanData });
                        string tip = "";
                        if (coreName == "vanilla")
                        {
                            tip = "\n[提示] 注意：'vanilla' 仅收录原版正式发布版。官方快照版/预览版（如 26.4-snapshot-3、26.3-rc 等）在镜像站中独立作为 'vanilla-snapshot' 端提供，请传入 core_name='vanilla-snapshot' 查询！";
                        }
                        return succ && cleanData != null
                            ? $"[SUCCESS] 核心 [{coreName}] API 实时返回的支持版本列表:\n{cleanData.ToJsonString()}{tip}"
                            : $"[ERROR] 查询核心 [{coreName}] 支持版本失败: {msg}";
                    }

                    // 3. 留空：全量动态返回当前 API 提供的分类与核心列表
                    {
                        var (succ, data, msg) = await GetMslApiDataAsync("/mirrors");
                        var cleanData = ConvertJTokenToJsonNode(data);
                        await onToolExecuted("query_available_server_cores", new { query = "all_cores", success = succ, data = cleanData });
                        return succ && cleanData != null
                            ? $"[SUCCESS] MSL 镜像站 API 当前实时提供的全量核心分类与类型列表（100% 动态）：\n{cleanData.ToJsonString()}\n[提示] 查找特定版本时可直接传入 version 参数（如 version='26.4'），工具会自动动态遍历 API 检索全量核心！"
                            : $"[ERROR] 查询镜像核心分类失败: {msg}";
                    }
                }

                case "create_mc_server":
                {
                    string type = args?["server_type"]?.ToString() ?? "paper";
                    string version = args?["mc_version"]?.ToString() ?? "1.20.2";

                    if (type.Equals("snapshot", StringComparison.OrdinalIgnoreCase) ||
                        type.Equals("vanilla_snapshot", StringComparison.OrdinalIgnoreCase) ||
                        type.Equals("vanillasnapshot", StringComparison.OrdinalIgnoreCase))
                    {
                        type = "vanilla-snapshot";
                    }

                    // 自动容错：如果用户/大模型传了 vanilla 核心，但版本号其实是快照版（包含 snapshot、-rc、-pre），自动矫正为 vanilla-snapshot
                    if (type.Equals("vanilla", StringComparison.OrdinalIgnoreCase) &&
                        (version.Contains("snapshot", StringComparison.OrdinalIgnoreCase) ||
                         version.Contains("-rc", StringComparison.OrdinalIgnoreCase) ||
                         version.Contains("-pre", StringComparison.OrdinalIgnoreCase)))
                    {
                        type = "vanilla-snapshot";
                    }

                    string serverName = args?["server_name"]?.ToString() ?? $"{type.ToUpper()}-{version}";
                    string specifiedJava = args?["java"]?.ToString() ?? "";

                    int maxM = 2048;
                    if (args != null && args.ContainsKey("max_m") && args["max_m"] != null)
                        int.TryParse(args["max_m"].ToString(), out maxM);

                    int minM = 1024;
                    if (args != null && args.ContainsKey("min_m") && args["min_m"] != null)
                        int.TryParse(args["min_m"].ToString(), out minM);

                    string jvmArgs = args?["args"]?.ToString() ?? "";
                    bool ignoreEula = true;
                    if (args != null && args.ContainsKey("ignore_eula") && args["ignore_eula"] != null)
                        bool.TryParse(args["ignore_eula"].ToString(), out ignoreEula);

                    string serverPort = args?["server_port"]?.ToString() ?? "25565";
                    string customPath = args?["path"]?.ToString() ?? "";

                    // 整合包 / 压缩包模式参数
                    string packageLocalPath = args?["package_local_path"]?.ToString()?.Trim() ?? "";
                    string packageUrl = args?["package_url"]?.ToString()?.Trim() ?? "";
                    string packageFileKey = args?["package_file_key"]?.ToString()?.Trim() ?? "";

                    // MCDR 模式参数
                    bool isMcdr = false;
                    if (args != null && args.ContainsKey("mcdr") && args["mcdr"] != null)
                        bool.TryParse(args["mcdr"].ToString(), out isMcdr);

                    string mcdrPython = args?["mcdr_python"]?.ToString() ?? "python";
                    string mcdrHandler = args?["mcdr_handler"]?.ToString() ?? "";
                    bool mcdrInstall = true;
                    if (args != null && args.ContainsKey("mcdr_install") && args["mcdr_install"] != null)
                        bool.TryParse(args["mcdr_install"].ToString(), out mcdrInstall);

                    string mcdrPipMirror = args?["mcdr_pip_mirror"]?.ToString() ?? "";

                    // 自定义核心 / 核心直链 / 核心上传参数
                    string customCoreUrl = args?["core_url"]?.ToString()?.Trim() ?? "";
                    string customCoreFileName = args?["core_filename"]?.ToString()?.Trim() ?? "";
                    string coreFileKey = args?["core_file_key"]?.ToString()?.Trim() ?? "";
                    string coreLocalPath = args?["core_local_path"]?.ToString()?.Trim() ?? "";

                    if (!string.IsNullOrWhiteSpace(coreLocalPath))
                    {
                        if (!string.IsNullOrWhiteSpace(coreFileKey))
                            return "[ERROR] 不能同时提供 core_file_key 和 core_local_path。";
                        if (!File.Exists(coreLocalPath))
                            return $"[ERROR] 本地核心文件不存在: {coreLocalPath}";
                        var stagedCore = await _uploadService.StageLocalFileAsync(
                            coreLocalPath,
                            string.IsNullOrWhiteSpace(customCoreFileName) ? Path.GetFileName(coreLocalPath) : customCoreFileName,
                            userId);
                        coreFileKey = stagedCore.UploadId;
                        if (string.IsNullOrWhiteSpace(customCoreFileName))
                            customCoreFileName = stagedCore.FileName;
                    }

                    if (!string.IsNullOrWhiteSpace(coreFileKey))
                    {
                        if (!McpUploadService.IsValidUploadId(coreFileKey) || !_uploadService.IsOwned(coreFileKey, userId) || !_uploadService.StagedFileExists(coreFileKey))
                            return "[ERROR] core_file_key 无效、已过期或无权访问。";
                        if (!string.IsNullOrWhiteSpace(packageFileKey) || !string.IsNullOrWhiteSpace(packageLocalPath) ||
                            !string.IsNullOrWhiteSpace(packageUrl) || !string.IsNullOrWhiteSpace(customCoreUrl))
                            return "[ERROR] 单核心文件上传 (core_file_key) 不能与整合包或远程核心下载 (core_url) 同时使用。";
                        if (string.IsNullOrWhiteSpace(customCoreFileName) && _uploadService.TryGetRecord(coreFileKey, out var coreRec) && coreRec != null)
                            customCoreFileName = coreRec.FileName;
                    }

                    if (!string.IsNullOrWhiteSpace(packageFileKey))
                    {
                        if (!McpUploadService.IsValidUploadId(packageFileKey) || !_uploadService.IsOwned(packageFileKey, userId) || !_uploadService.StagedFileExists(packageFileKey))
                            return "[ERROR] package_file_key 无效、已过期或无权访问。";
                        if (!string.IsNullOrWhiteSpace(packageUrl) || !string.IsNullOrWhiteSpace(packageLocalPath))
                            return "[ERROR] 整合包上传 (package_file_key) 不能与远程下载 (package_url) 或本机路径 (package_local_path) 同时使用。";
                    }

                    int recJavaMajor = GetRecommendedJavaMajor(version);

                    string selectedJava = specifiedJava;
                    if (string.IsNullOrWhiteSpace(selectedJava) || selectedJava.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var javaScanner = scope.ServiceProvider.GetRequiredService<IJavaScannerService>();
                        var scannedJavaList = await javaScanner.ScanJavaAsync(false);

                        // 1. 优先在现有列表中寻找精确匹配
                        var exactMatch = scannedJavaList?.FirstOrDefault(j => {
                            return int.TryParse(GetJavaMajorVersion(j.Version), out int m) && m == recJavaMajor;
                        });

                        // 2. 若未在缓存中命中，立即执行强制全盘重扫（避免用户新安装的 JDK 未被感知）
                        if (exactMatch == null)
                        {
                            _logger.LogInformation("未在缓存中找到 Java {Major}，正在强制全盘重新扫描本地 Java 环境...", recJavaMajor);
                            scannedJavaList = await javaScanner.ScanJavaAsync(true);
                            exactMatch = scannedJavaList?.FirstOrDefault(j => {
                                return int.TryParse(GetJavaMajorVersion(j.Version), out int m) && m == recJavaMajor;
                            });
                        }

                        if (exactMatch != null && File.Exists(exactMatch.Path))
                        {
                            selectedJava = exactMatch.Path;
                            _logger.LogInformation("找到本地匹配的 Java {Major}: {Path}", recJavaMajor, exactMatch.Path);
                        }
                        else
                        {
                            // 3. 本地未安装目标版本：绝不能降级到低版本（如用 Java 21 运行 MC 26 会直接导致启动崩溃）！
                            // 直接设定为 MSLX 官方协议标识 "MSLX://Java/{recJavaMajor}"，
                            // 宿主 ServerCreationService.EnsureJavaAsync 会在建服过程中自动从官方镜像站下载部署该 Java！
                            selectedJava = $"MSLX://Java/{recJavaMajor}";
                            _logger.LogInformation("本地全盘未找到匹配的 Java {Major}，将启用 MSLX 在线 Java 机制，自动部署 MSLX://Java/{Major}", recJavaMajor, recJavaMajor);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(selectedJava))
                    {
                        selectedJava = "auto";
                    }

                    string? coreUrl = string.IsNullOrWhiteSpace(customCoreUrl) ? null : customCoreUrl;
                    string? coreSha256 = null;
                    string coreFileName = string.IsNullOrWhiteSpace(customCoreFileName) ? $"{type}-{version}.jar" : customCoreFileName;

                    if (!string.IsNullOrWhiteSpace(packageFileKey) && string.IsNullOrWhiteSpace(customCoreFileName))
                    {
                        try
                        {
                            var inspection = _uploadService.InspectArchive(McpUploadService.GetStagedFilePath(packageFileKey));
                            if (!inspection.IsMrpack && inspection.CandidateCores.Count > 0)
                            {
                                coreFileName = inspection.CandidateCores[0];
                            }
                            else if (inspection.IsMrpack)
                            {
                                coreFileName = "server.jar";
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "检查已上传整合包候选核心失败: {PackageFileKey}", packageFileKey);
                        }
                    }

                    bool hasExplicitPackageOrCore = !string.IsNullOrWhiteSpace(coreUrl)
                        || !string.IsNullOrWhiteSpace(coreFileKey)
                        || !string.IsNullOrWhiteSpace(packageFileKey)
                        || !string.IsNullOrWhiteSpace(packageLocalPath)
                        || !string.IsNullOrWhiteSpace(packageUrl);

                    if (!hasExplicitPackageOrCore)
                    {
                        try
                        {
                            var (apiSuccess, apiData, _) = await GetMslApiDataAsync($"/download/server/{type.ToLower()}/{version}?build=latest");
                            if (apiSuccess && apiData is JToken token)
                            {
                                coreUrl = token["url"]?.ToString();
                                coreSha256 = token["sha256"]?.ToString();
                                if (token["name"] != null && !string.IsNullOrEmpty(token["name"].ToString()))
                                {
                                    coreFileName = token["name"].ToString();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "向镜像站查询核心下载地址失败: {Type} {Version}", type, version);
                        }
                    }

                    var serverId = MSLX.SDK.MSLX.Config.Servers.GenerateServerId();
                    var createRequest = new CreateServerRequest
                    {
                        name = serverName,
                        core = coreFileName,
                        coreUrl = coreUrl,
                        coreSha256 = coreSha256,
                        coreFileKey = string.IsNullOrWhiteSpace(coreFileKey) ? null : coreFileKey,
                        java = selectedJava,
                        maxM = maxM,
                        minM = minM,
                        args = string.IsNullOrWhiteSpace(jvmArgs) ? null : jvmArgs,
                        ignoreEula = ignoreEula,
                        path = string.IsNullOrWhiteSpace(customPath) ? null : customPath,
                        DockerPorts = $"{serverPort}:{serverPort}",
                        packageLocalPath = string.IsNullOrWhiteSpace(packageLocalPath) ? null : packageLocalPath,
                        packageUrl = string.IsNullOrWhiteSpace(packageUrl) ? null : packageUrl,
                        packageFileKey = string.IsNullOrWhiteSpace(packageFileKey) ? null : packageFileKey,
                        mcdr = isMcdr,
                        mcdrPython = isMcdr ? mcdrPython : null,
                        mcdrHandler = isMcdr && !string.IsNullOrWhiteSpace(mcdrHandler) ? mcdrHandler : null,
                        mcdrInstall = mcdrInstall,
                        mcdrPipMirror = string.IsNullOrWhiteSpace(mcdrPipMirror) ? null : mcdrPipMirror
                    };

                    string bgTaskId = "";
                    try
                    {
                        var bgTaskManager = _serviceProvider.GetService<IBackgroundTaskManager>()
                            ?? MSLX.SDK.MSLX.Tasks;
                        if (bgTaskManager != null)
                        {
                            var (bgTask, _) = bgTaskManager.CreateTask(
                                userId: userId,
                                instanceId: serverId,
                                type: MSLX.SDK.Models.Files.TaskType.CreateServer,
                                title: $"创建实例 {serverName}",
                                targetName: coreFileName
                            );
                            bgTaskId = bgTask.Id;
                            _logger.LogInformation("[AiService] 已创建全局后台任务记录: TaskId={TaskId}, ServerId={ServerId}, UserId={UserId}", bgTaskId, serverId, userId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[AiService] 创建后台任务记录失败");
                    }

                    var task = new CreateServerTask
                    {
                        ServerId = serverId.ToString(),
                        Request = createRequest,
                        BackgroundTaskId = bgTaskId,
                        UserId = userId
                    };

                    bool queued = await QueueCreateServerTaskAsync(_serviceProvider, task);
                    if (!queued)
                    {
                        _logger.LogError("[AiService] 提交创建服务器任务到后台队列失败。ServerId: {ServerId}", serverId);
                        return $"[ERROR] 提交创建服务器任务失败：无法将任务放入后台队列，请检查系统后台服务是否正常。";
                    }

                    int maxWaitMs = 300000;
                    int intervalMs = 1000;
                    CacheableStatus? finalStatus = null;

                    while (maxWaitMs > 0)
                    {
                        await Task.Delay(intervalMs);
                        maxWaitMs -= intervalMs;

                        if (_memoryCache.TryGetValue(serverId.ToString(), out CacheableStatus? cacheStatus) && cacheStatus != null)
                        {
                            finalStatus = cacheStatus;
                            if (cacheStatus.Progress >= 100 || cacheStatus.Message.Contains("完成") || cacheStatus.Message.Contains("失败") || cacheStatus.Progress < 0)
                            {
                                break;
                            }
                        }

                        if (!string.IsNullOrEmpty(bgTaskId))
                        {
                            try
                            {
                                var taskItem = MSLX.SDK.MSLX.Tasks?.GetTask(bgTaskId);
                                if (taskItem != null)
                                {
                                    if (taskItem.State == MSLX.SDK.Models.Files.TaskState.Success)
                                    {
                                        finalStatus ??= new CacheableStatus { Message = taskItem.Message ?? "部署完成", Progress = 100 };
                                        break;
                                    }
                                    else if (taskItem.State == MSLX.SDK.Models.Files.TaskState.Failed || taskItem.State == MSLX.SDK.Models.Files.TaskState.Canceled)
                                    {
                                        finalStatus = new CacheableStatus { Message = taskItem.Message ?? "任务失败或已取消", Progress = -1 };
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    bool isSuccess = finalStatus != null && (finalStatus.Progress >= 100 || finalStatus.Message.Contains("完成"));
                    bool isFailure = finalStatus != null && (finalStatus.Progress < 0 || finalStatus.Message.Contains("失败") || finalStatus.Message.Contains("中断") || finalStatus.Message.Contains("错误"));

                    var resData = new
                    {
                        serverId = serverId,
                        name = serverName,
                        type = type,
                        version = version,
                        recommendedJavaMajor = recJavaMajor,
                        java = selectedJava,
                        status = finalStatus?.Message ?? "started",
                        backgroundTaskId = bgTaskId,
                        progress = finalStatus?.Progress ?? 0
                    };
                    await onToolExecuted("create_mc_server", resData);

                    if (isFailure)
                    {
                        return $"[ERROR] 服务器 (ID: {serverId}) 后台部署失败: {finalStatus?.Message ?? "部署过程发生错误"}";
                    }
                    else if (!isSuccess)
                    {
                        return $"[INFO] 服务器 (ID: {serverId}) 的部署仍在后台运行，请使用 query_creation_status 查询进度。";
                    }
                    else
                    {
                        return $"[SUCCESS] 服务器 (ID: {serverId}, 名称: {serverName}) 核心包部署安装已全量成功完成 (自动为 MC {version} 推荐并绑定 Java {recJavaMajor})！请立刻接着调用 write_server_file 或 update_server_config 为用户修改配置文件。";
                    }
                }

                case "read_server_file":
                {
                    int targetId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                    {
                        int.TryParse(args["server_id"].ToString(), out targetId);
                    }
                    string filePath = args?["file_path"]?.ToString() ?? "";

                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        return "[ERROR] file_path 不能为空。";
                    }

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    var targetServer = targetId > 0
                        ? serverList.FirstOrDefault(s => s.ID == targetId)
                        : serverList.OrderByDescending(s => s.ID).FirstOrDefault();

                    if (targetServer == null)
                    {
                        return "[ERROR] 未找到目标服务器实例。";
                    }

                    string serverDir = targetServer.Base;
                    if (string.IsNullOrEmpty(serverDir) || !Directory.Exists(serverDir))
                    {
                        serverDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Servers", targetServer.ID.ToString());
                    }

                    string safeBaseDir = Path.GetFullPath(serverDir);
                    string targetFilePath = Path.GetFullPath(Path.Combine(safeBaseDir, filePath));

                    if (!IsWithinServerDirectory(safeBaseDir, targetFilePath))
                    {
                        return "[ERROR] 越权访问：不允许读取服务器实例目录之外的文件。";
                    }

                    if (!File.Exists(targetFilePath))
                    {
                        return $"[ERROR] 文件不存在: {filePath}";
                    }

                    string content;
                    using (var fs = new FileStream(targetFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        if (fs.Length > 200 * 1024)
                        {
                            fs.Seek(-200 * 1024, SeekOrigin.End);
                            sr.DiscardBufferedData();
                            content = "……(日志文件过大，已自动截取最新末尾日志)……\n" + await sr.ReadToEndAsync();
                        }
                        else
                        {
                            content = await sr.ReadToEndAsync();
                        }
                    }

                    var readResult = new { serverId = targetServer.ID, filePath = filePath, length = content.Length };
                    await onToolExecuted("read_server_file", readResult);
                    return $"[SUCCESS] 读取文件 {filePath} 成功：\n```\n{content}\n```";
                }

                case "write_server_file":
                {
                    int targetId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                    {
                        int.TryParse(args["server_id"].ToString(), out targetId);
                    }
                    string filePath = args?["file_path"]?.ToString() ?? "";
                    string content = args?["content"]?.ToString() ?? "";
                    bool isConfirmed = assumeConfirmed;

                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        return "[ERROR] file_path 不能为空。";
                    }

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    var targetServer = targetId > 0
                        ? serverList.FirstOrDefault(s => s.ID == targetId)
                        : serverList.OrderByDescending(s => s.ID).FirstOrDefault();

                    if (targetServer == null)
                    {
                        return "[ERROR] 未找到目标服务器实例。";
                    }

                    string serverDir = targetServer.Base;
                    if (string.IsNullOrEmpty(serverDir) || !Directory.Exists(serverDir))
                    {
                        serverDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Servers", targetServer.ID.ToString());
                        Directory.CreateDirectory(serverDir);
                    }

                    string safeBaseDir = Path.GetFullPath(serverDir);
                    string targetFilePath = Path.GetFullPath(Path.Combine(safeBaseDir, filePath));

                    if (!IsWithinServerDirectory(safeBaseDir, targetFilePath))
                    {
                        return "[ERROR] 越权操作：不允许写文件到服务器实例目录之外。";
                    }

                    bool exists = File.Exists(targetFilePath);
                    if (exists && !isConfirmed)
                    {


                        string confirmationId = CreatePendingOperation("write_server_file", argsJson, targetServer.ID, filePath, "edit", userId);
                        var needConfirmData = new { serverId = targetServer.ID, filePath = filePath, action = "edit", requiresConfirmation = true, confirmed = false, confirmationId = confirmationId, contentLength = content.Length };
                        await onToolExecuted("write_server_file", needConfirmData);
                        return PendingMessage(confirmationId);
                    }

                    string? parentDir = Path.GetDirectoryName(targetFilePath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    await using (var stream = new FileStream(targetFilePath,
                        isConfirmed ? FileMode.Truncate : FileMode.CreateNew, FileAccess.Write))
                    await using (var writer = new StreamWriter(stream))
                        await writer.WriteAsync(content);
                    var writeResult = new { serverId = targetServer.ID, filePath = filePath, contentLength = content.Length, confirmed = true };
                    await onToolExecuted("write_server_file", writeResult);
                    return $"[SUCCESS] 成功写入/更新服务器 {targetServer.ID} 的文件 `{filePath}`！";
                }

                case "delete_server_file":
                {
                    int targetId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                    {
                        int.TryParse(args["server_id"].ToString(), out targetId);
                    }
                    string filePath = args?["file_path"]?.ToString() ?? "";
                    bool isConfirmed = assumeConfirmed;

                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        return "[ERROR] file_path 不能为空。";
                    }

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    var targetServer = targetId > 0
                        ? serverList.FirstOrDefault(s => s.ID == targetId)
                        : serverList.OrderByDescending(s => s.ID).FirstOrDefault();

                    if (targetServer == null)
                    {
                        return "[ERROR] 未找到目标服务器实例。";
                    }

                    string serverDir = targetServer.Base;
                    if (string.IsNullOrEmpty(serverDir) || !Directory.Exists(serverDir))
                    {
                        serverDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Servers", targetServer.ID.ToString());
                    }

                    string safeBaseDir = Path.GetFullPath(serverDir);
                    string targetPath = Path.GetFullPath(Path.Combine(safeBaseDir, filePath));

                    if (!IsWithinServerDirectory(safeBaseDir, targetPath) ||
                        Path.GetRelativePath(safeBaseDir, targetPath) == ".")
                    {
                        return "[ERROR] 请选择实例内的文件或目录。删除整个实例请使用实例删除工具。";
                    }

                    if (!isConfirmed)
                    {


                        string confirmationId = CreatePendingOperation("delete_server_file", argsJson, targetServer.ID, filePath, "delete", userId);
                        var confirmData = new { serverId = targetServer.ID, filePath = filePath, action = "delete", requiresConfirmation = true, confirmed = false, confirmationId = confirmationId };
                        await onToolExecuted("delete_server_file", confirmData);
                        return PendingMessage(confirmationId);
                    }

                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }
                    else if (Directory.Exists(targetPath))
                    {
                        Directory.Delete(targetPath, true);
                    }
                    else
                    {
                        return $"[ERROR] 要删除的文件/目录不存在: {filePath}";
                    }

                    var deleteResult = new { serverId = targetServer.ID, filePath = filePath, action = "delete", confirmed = true };
                    await onToolExecuted("delete_server_file", deleteResult);
                    return $"[SUCCESS] 已成功彻底删除服务器 {targetServer.ID} 的 `{filePath}`！";
                }

                case "update_server_config":
                {
                    int targetId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                    {
                        int.TryParse(args["server_id"].ToString(), out targetId);
                    }

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    var targetServer = targetId > 0
                        ? serverList.FirstOrDefault(s => s.ID == targetId)
                        : serverList.OrderByDescending(s => s.ID).FirstOrDefault();

                    if (targetServer == null)
                    {
                        return "[ERROR] 未找到目标服务器实例。";
                    }

                    string serverDir = targetServer.Base;
                    if (string.IsNullOrEmpty(serverDir) || !Directory.Exists(serverDir))
                    {
                        serverDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Servers", targetServer.ID.ToString());
                        Directory.CreateDirectory(serverDir);
                    }

                    string propsPath = Path.Combine(serverDir, "server.properties");
                    var propsDict = new Dictionary<string, string>();
                    if (File.Exists(propsPath))
                    {
                        using (var fs = new FileStream(propsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs, Encoding.UTF8))
                        {
                            string? line;
                            while ((line = await sr.ReadLineAsync()) != null)
                            {
                                if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("#") && line.Contains('='))
                                {
                                    var parts = line.Split('=', 2);
                                    propsDict[parts[0].Trim()] = parts[1].Trim();
                                }
                            }
                        }
                    }

                    if (args != null && args.ContainsKey("online_mode") && args["online_mode"] != null)
                    {
                        propsDict["online-mode"] = args["online_mode"].GetValue<bool>() ? "true" : "false";
                    }
                    if (args != null && args.ContainsKey("pvp") && args["pvp"] != null)
                    {
                        propsDict["pvp"] = args["pvp"].GetValue<bool>() ? "true" : "false";
                    }
                    if (args != null && args.ContainsKey("max_players") && args["max_players"] != null)
                    {
                        propsDict["max-players"] = args["max_players"].ToString();
                    }
                    if (args != null && args.ContainsKey("server_port") && args["server_port"] != null)
                    {
                        propsDict["server-port"] = args["server_port"].ToString();
                    }
                    if (args != null && args.ContainsKey("motd") && args["motd"] != null)
                    {
                        propsDict["motd"] = args["motd"].ToString();
                    }

                    var sb = new StringBuilder("#Minecraft server properties\n");
                    foreach (var kvp in propsDict)
                    {
                        sb.AppendLine($"{kvp.Key}={kvp.Value}");
                    }
                    File.WriteAllText(propsPath, sb.ToString());

                    var updateResult = new { serverId = targetServer.ID, serverName = targetServer.Name, properties = propsDict };
                    await onToolExecuted("update_server_config", updateResult);
                    return $"[SUCCESS] 服务器 (ID: {targetServer.ID}, 名称: {targetServer.Name}) 的 `server.properties` 配置更新成功！";
                }

                case "control_server":
                {
                    uint serverId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                    {
                        uint.TryParse(args["server_id"].ToString(), out serverId);
                    }
                    string action = args?["action"]?.ToString()?.ToLower() ?? "start";

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    if (serverId == 0 && serverList.Count > 0)
                    {
                        serverId = (uint)serverList.OrderByDescending(s => s.ID).First().ID;
                    }

                    if (serverId == 0) return "[ERROR] 未指定有效的 server_id，且当前没有任何服务器实例。";

                    if (action == "start")
                    {
                        var (succ, msg) = _lifecycleService.StartServer(serverId);
                        await onToolExecuted("control_server", new { serverId, action, succ, msg });
                        return succ ? $"[SUCCESS] 服务器 {serverId} 启动指令已发送。" : $"[ERROR] 启动服务器 {serverId} 失败: {msg}";
                    }
                    else if (action == "stop")
                    {
                        bool stopped = _lifecycleService.StopServer(serverId);
                        await onToolExecuted("control_server", new { serverId, action, stopped });
                        return stopped ? $"[SUCCESS] 服务器 {serverId} 停止指令已发送。" : $"[ERROR] 停止服务器 {serverId} 失败。";
                    }
                    else if (action == "restart")
                    {
                        var (succ, msg) = await _lifecycleService.RestartServer(serverId);
                        await onToolExecuted("control_server", new { serverId, action, succ, msg });
                        return succ ? $"[SUCCESS] 服务器 {serverId} 重启指令已完成。" : $"[ERROR] 重启服务器 {serverId} 失败: {msg}";
                    }

                    return "[ERROR] 未知的控制动作。";
                }

                case "query_server_status":
                {
                    var servers = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    if (int.TryParse(args?["server_id"]?.ToString(), out var serverId) && serverId > 0)
                    {
                        servers = servers.Where(server => server.ID == serverId).ToList();
                        if (servers.Count == 0)
                            return $"[ERROR] 未找到服务器实例 #{serverId}。";
                    }

                    var list = new List<object>();
                    foreach (var s in servers)
                    {
                        var (status, desc) = _lifecycleService.GetServerStatus((uint)s.ID);
                        list.Add(new
                        {
                            id = s.ID,
                            name = s.Name,
                            core = s.Core,
                            status = desc
                        });
                    }
                    await onToolExecuted("query_server_status", list);
                    return $"[SUCCESS] 当前服务器列表与状态:\n" + JsonSerializer.Serialize(list);
                }

                case "read_server_log":
                {
                    int targetId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                        int.TryParse(args["server_id"].ToString(), out targetId);

                    string logType = (args != null && args.ContainsKey("log_type") && args["log_type"] != null)
                        ? args["log_type"].ToString().Trim()
                        : "latest";

                    int tailLines = 100;
                    if (args != null && args.ContainsKey("tail_lines") && args["tail_lines"] != null)
                        int.TryParse(args["tail_lines"].ToString(), out tailLines);

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    var targetServer = targetId > 0
                        ? serverList.FirstOrDefault(s => s.ID == targetId)
                        : serverList.OrderByDescending(s => s.ID).FirstOrDefault();

                    if (targetServer == null) return "[ERROR] 未找到目标服务器实例。";

                    string serverDir = targetServer.Base;
                    if (string.IsNullOrEmpty(serverDir) || !Directory.Exists(serverDir))
                    {
                        serverDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Servers", targetServer.ID.ToString());
                    }

                    string targetFilePath = "";
                    if (logType.Equals("crash_report", StringComparison.OrdinalIgnoreCase))
                    {
                        string crashDir = Path.Combine(serverDir, "crash-reports");
                        if (!Directory.Exists(crashDir)) return "[INFO] 未找到 crash-reports 崩溃日志目录，服务器暂无崩溃记录。";

                        var latestCrashFile = new DirectoryInfo(crashDir)
                            .GetFiles("*.txt")
                            .OrderByDescending(f => f.LastWriteTime)
                            .FirstOrDefault();

                        if (latestCrashFile == null) return "[INFO] crash-reports 目录下暂无崩溃文件。";
                        targetFilePath = latestCrashFile.FullName;
                    }
                    else
                    {
                        targetFilePath = Path.Combine(serverDir, "logs", "latest.log");
                        if (!File.Exists(targetFilePath))
                        {
                            var consoleLogs = _consoleService.GetLogs((uint)targetServer.ID);
                            if (consoleLogs.Count > 0)
                            {
                                var selectedConsoleLines = consoleLogs.TakeLast(Math.Min(tailLines, 300)).ToList();
                                var resultConsole = string.Join("\n", selectedConsoleLines);
                                var resConsoleData = new { file = "console_history", linesCount = selectedConsoleLines.Count, content = resultConsole };
                                await onToolExecuted("read_server_log", resConsoleData);
                                return $"[SUCCESS] 成功提取控制台历史末尾 {selectedConsoleLines.Count} 行内容：\n\n```log\n{resultConsole}\n```";
                            }
                            return "[INFO] 未找到 logs/latest.log 运行日志文件。";
                        }
                    }

                    try
                    {
                        using var fs = new FileStream(targetFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs, Encoding.UTF8);
                        var lines = new List<string>();
                        string? line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            lines.Add(line);
                        }

                        var selectedLines = lines.TakeLast(Math.Min(tailLines, 300)).ToList();
                        var resultText = string.Join("\n", selectedLines);
                        var resData = new { file = Path.GetFileName(targetFilePath), linesCount = selectedLines.Count, content = resultText };
                        await onToolExecuted("read_server_log", resData);
                        return $"[SUCCESS] 成功提取日志 {Path.GetFileName(targetFilePath)} 末尾 {selectedLines.Count} 行内容：\n\n```log\n{resultText}\n```";
                    }
                    catch (Exception ex)
                    {
                        return $"[ERROR] 读取日志文件失败: {ex.Message}";
                    }
                }

                case "copy_server_file":
                {
                    int targetId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                        int.TryParse(args["server_id"].ToString(), out targetId);

                    string srcRel = args?["source_path"]?.ToString().Trim() ?? "";
                    string dstRel = args?["destination_path"]?.ToString().Trim() ?? "";

                    if (string.IsNullOrWhiteSpace(srcRel) || string.IsNullOrWhiteSpace(dstRel))
                        return "[ERROR] 参数缺失：source_path 和 destination_path 不能为空。";

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    var targetServer = targetId > 0
                        ? serverList.FirstOrDefault(s => s.ID == targetId)
                        : serverList.OrderByDescending(s => s.ID).FirstOrDefault();

                    if (targetServer == null) return "[ERROR] 未找到目标服务器实例。";

                    string serverDir = targetServer.Base;
                    if (string.IsNullOrEmpty(serverDir) || !Directory.Exists(serverDir))
                    {
                        serverDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Servers", targetServer.ID.ToString());
                    }

                    string srcPath = Path.GetFullPath(Path.Combine(serverDir, srcRel));
                    string dstPath = Path.GetFullPath(Path.Combine(serverDir, dstRel));

                    if (!IsWithinServerDirectory(serverDir, srcPath) ||
                        !IsWithinServerDirectory(serverDir, dstPath))
                        return "[ERROR] 越权访问：文件路径超出目标服务器根目录。";

                    if (!assumeConfirmed && (File.Exists(dstPath) || Directory.Exists(dstPath)))
                        return await RequestReplacementAsync("copy_server_file", argsJson, targetServer.ID, dstRel, userId, onToolExecuted);

                    if (File.Exists(srcPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                        File.Copy(srcPath, dstPath, overwrite: assumeConfirmed);
                        var resData = new { action = "copy", source = srcRel, destination = dstRel };
                        await onToolExecuted("copy_server_file", resData);
                        return $"[SUCCESS] 成功复制文件: {srcRel} ➔ {dstRel}";
                    }
                    else if (Directory.Exists(srcPath))
                    {
                        CopyDirectoryRecursively(srcPath, dstPath, assumeConfirmed);
                        var resData = new { action = "copy_dir", source = srcRel, destination = dstRel };
                        await onToolExecuted("copy_server_file", resData);
                        return $"[SUCCESS] 成功复制目录: {srcRel} ➔ {dstRel}";
                    }
                    return $"[ERROR] 源文件或目录不存在: {srcRel}";
                }

                case "move_server_file":
                {
                    int targetId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                        int.TryParse(args["server_id"].ToString(), out targetId);

                    string srcRel = args?["source_path"]?.ToString().Trim() ?? "";
                    string dstRel = args?["destination_path"]?.ToString().Trim() ?? "";

                    if (string.IsNullOrWhiteSpace(srcRel) || string.IsNullOrWhiteSpace(dstRel))
                        return "[ERROR] 参数缺失：source_path 和 destination_path 不能为空。";

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    var targetServer = targetId > 0
                        ? serverList.FirstOrDefault(s => s.ID == targetId)
                        : serverList.OrderByDescending(s => s.ID).FirstOrDefault();

                    if (targetServer == null) return "[ERROR] 未找到目标服务器实例。";

                    string serverDir = targetServer.Base;
                    if (string.IsNullOrEmpty(serverDir) || !Directory.Exists(serverDir))
                    {
                        serverDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Servers", targetServer.ID.ToString());
                    }

                    string srcPath = Path.GetFullPath(Path.Combine(serverDir, srcRel));
                    string dstPath = Path.GetFullPath(Path.Combine(serverDir, dstRel));

                    if (!IsWithinServerDirectory(serverDir, srcPath) ||
                        !IsWithinServerDirectory(serverDir, dstPath))
                        return "[ERROR] 越权访问：文件路径超出目标服务器根目录。";

                    if (!assumeConfirmed && (File.Exists(dstPath) || Directory.Exists(dstPath)))
                        return await RequestReplacementAsync("move_server_file", argsJson, targetServer.ID, dstRel, userId, onToolExecuted);

                    if (File.Exists(srcPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                        File.Move(srcPath, dstPath, overwrite: assumeConfirmed);
                        var resData = new { action = "move", source = srcRel, destination = dstRel };
                        await onToolExecuted("move_server_file", resData);
                        return $"[SUCCESS] 成功移动/重命名文件: {srcRel} ➔ {dstRel}";
                    }
                    else if (Directory.Exists(srcPath))
                    {
                        if (assumeConfirmed && Directory.Exists(dstPath)) Directory.Delete(dstPath, true);
                        Directory.Move(srcPath, dstPath);
                        var resData = new { action = "move_dir", source = srcRel, destination = dstRel };
                        await onToolExecuted("move_server_file", resData);
                        return $"[SUCCESS] 成功移动/重命名目录: {srcRel} ➔ {dstRel}";
                    }
                    return $"[ERROR] 源文件或目录不存在: {srcRel}";
                }

                case "query_system_metrics":
                {
                    int targetId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                        int.TryParse(args["server_id"].ToString(), out targetId);

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    var targetServer = targetId > 0
                        ? serverList.FirstOrDefault(s => s.ID == targetId)
                        : serverList.OrderByDescending(s => s.ID).FirstOrDefault();

                    var gcMemoryMb = GC.GetTotalMemory(false) / 1024 / 1024;
                    var process = System.Diagnostics.Process.GetCurrentProcess();
                    var processWorkingSetMb = process.WorkingSet64 / 1024 / 1024;

                    var resData = new
                    {
                        systemMemoryMb = processWorkingSetMb,
                        gcMemoryMb = gcMemoryMb,
                        targetServerId = targetServer?.ID,
                        targetServerName = targetServer?.Name,
                        targetServerJava = targetServer?.Java,
                        targetServerMemory = targetServer?.MaxM
                    };

                    await onToolExecuted("query_system_metrics", resData);
                    return $"[SUCCESS] 系统性能状态：宿主机 Daemon 进程内存使用 {processWorkingSetMb}MB (GC堆: {gcMemoryMb}MB)；当前服务器 #{targetServer?.ID ?? 0} [{targetServer?.Name}] 设定内存: {targetServer?.MaxM?.ToString() ?? "默认"}MB，绑定Java: {targetServer?.Java ?? "默认"}。";
                }

                case "list_server_mods_plugins":
                {
                    int targetId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                        int.TryParse(args["server_id"].ToString(), out targetId);

                    string folderType = args?["folder_type"]?.ToString().Trim() ?? "both";

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    var targetServer = targetId > 0
                        ? serverList.FirstOrDefault(s => s.ID == targetId)
                        : serverList.OrderByDescending(s => s.ID).FirstOrDefault();

                    if (targetServer == null) return "[ERROR] 未找到目标服务器实例。";

                    string serverDir = targetServer.Base;
                    if (string.IsNullOrEmpty(serverDir) || !Directory.Exists(serverDir))
                    {
                        serverDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Servers", targetServer.ID.ToString());
                    }

                    var mods = new List<object>();
                    var plugins = new List<object>();

                    if (folderType == "mods" || folderType == "both")
                    {
                        string modsDir = Path.Combine(serverDir, "mods");
                        if (Directory.Exists(modsDir))
                        {
                            var files = new DirectoryInfo(modsDir).GetFiles("*.*")
                                .Where(f => f.Extension.Equals(".jar", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".disabled", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                                .OrderBy(f => f.Name);

                            foreach (var f in files)
                            {
                                bool isEnabled = f.Extension.Equals(".jar", StringComparison.OrdinalIgnoreCase);
                                mods.Add(new { name = f.Name, sizeKb = f.Length / 1024, enabled = isEnabled, lastModified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") });
                            }
                        }
                    }

                    if (folderType == "plugins" || folderType == "both")
                    {
                        string pluginsDir = Path.Combine(serverDir, "plugins");
                        if (Directory.Exists(pluginsDir))
                        {
                            var files = new DirectoryInfo(pluginsDir).GetFiles("*.*")
                                .Where(f => f.Extension.Equals(".jar", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".disabled", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                                .OrderBy(f => f.Name);

                            foreach (var f in files)
                            {
                                bool isEnabled = f.Extension.Equals(".jar", StringComparison.OrdinalIgnoreCase);
                                plugins.Add(new { name = f.Name, sizeKb = f.Length / 1024, enabled = isEnabled, lastModified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") });
                            }
                        }
                    }

                    var resData = new { modsCount = mods.Count, pluginsCount = plugins.Count, mods = mods, plugins = plugins };
                    await onToolExecuted("list_server_mods_plugins", resData);
                    return $"[SUCCESS] 服务器 #{targetServer.ID} Mod 数量: {mods.Count} 个, 插件数量: {plugins.Count} 个。" +
                           (mods.Count > 0 ? "\n\nMod 列表:\n" + string.Join("\n", mods.Select(m => $"- {((dynamic)m).name} ({(((dynamic)m).enabled ? "✅ 已启用" : "❌ 已禁用")}, {((dynamic)m).sizeKb}KB)")) : "") +
                           (plugins.Count > 0 ? "\n\n插件列表:\n" + string.Join("\n", plugins.Select(p => $"- {((dynamic)p).name} ({(((dynamic)p).enabled ? "✅ 已启用" : "❌ 已禁用")}, {((dynamic)p).sizeKb}KB)")) : "");
                }

                case "send_server_command":
                {
                    uint serverId = 0;
                    if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                        uint.TryParse(args["server_id"].ToString(), out serverId);

                    string command = args?["command"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(command)) return "[ERROR] command 不能为空。";

                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    if (serverId == 0 && serverList.Count > 0)
                        serverId = (uint)serverList.OrderByDescending(s => s.ID).First().ID;

                    if (serverId == 0) return "[ERROR] 未指定有效的 server_id。";

                    bool success = _consoleService.SendCommand(serverId, command, true);
                    await onToolExecuted("send_server_command", new { serverId, command, success });
                    return success
                        ? $"[SUCCESS] 指令 `{command}` 已发送至服务器 #{serverId} 控制台。"
                        : $"[ERROR] 向服务器 #{serverId} 发送指令失败，服务器可能未运行。";
                }

                default:
                {
                    var opsRes = await HandleOpsToolAsync(name, args, onToolExecuted, assumeConfirmed, userId);
                    if (opsRes != null) return opsRes;
                    var backupRes = await HandleBackupFrpToolAsync(name, args, onToolExecuted, assumeConfirmed, userId);
                    if (backupRes != null) return backupRes;
                    var resRes = await HandleResourcesToolAsync(name, args, onToolExecuted, assumeConfirmed, userId);
                    if (resRes != null) return resRes;
                    var uploadRes = await HandleUploadToolAsync(name, args, onToolExecuted, assumeConfirmed, userId);
                    if (uploadRes != null) return uploadRes;
                    var terminalRes = await HandleTerminalToolAsync(name, args, onToolExecuted, assumeConfirmed, userId);
                    if (terminalRes != null) return terminalRes;

                    return $"[ERROR] 未找到工具处理逻辑 {name}";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行 Tool {Name} 异常", name);
            return $"[ERROR] 执行工具 {name} 失败: {ex.Message}";
        }
    }

    private async Task<bool> QueueCreateServerTaskAsync(IServiceProvider serviceProvider, CreateServerTask task)
    {
        try
        {
            object? targetQueue = null;

            // 1. 获取 CreateServerTask 对应的泛型队列类型 IBackgroundTaskQueue<CreateServerTask>
            var queueInterfaceType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("MSLX.Daemon.Utils.BackgroundTasks.IBackgroundTaskQueue`1"))
                .FirstOrDefault(t => t != null)?
                .MakeGenericType(typeof(CreateServerTask));

            // 2. 尝试从 PluginManager 获取宿主根 ServiceProvider
            IServiceProvider? rootServiceProvider = null;
            try
            {
                var pluginManagerType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("MSLX.Daemon.Services.PluginsService.PluginManager"))
                    .FirstOrDefault(t => t != null);

                if (pluginManagerType != null)
                {
                    var pluginManager = serviceProvider.GetService(pluginManagerType);
                    if (pluginManager != null)
                    {
                        var spField = pluginManagerType.GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic);
                        rootServiceProvider = spField?.GetValue(pluginManager) as IServiceProvider;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AiService] 尝试从 PluginManager 获取根 ServiceProvider 失败");
            }

            // 3. 策略 A：直接从宿主 ServerCreationService 中获取其活跃的 _taskQueue 实例
            if (rootServiceProvider != null)
            {
                try
                {
                    var hostedServices = rootServiceProvider.GetServices<IHostedService>();
                    var serverCreationService = hostedServices.FirstOrDefault(s => s.GetType().Name == "ServerCreationService");
                    if (serverCreationService != null)
                    {
                        var queueField = serverCreationService.GetType().GetField("_taskQueue", BindingFlags.Instance | BindingFlags.NonPublic);
                        targetQueue = queueField?.GetValue(serverCreationService);
                        if (targetQueue != null)
                        {
                            _logger.LogInformation("[AiService] 成功从 ServerCreationService 获取活跃任务队列实例");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AiService] 尝试从 ServerCreationService 提取 _taskQueue 异常");
                }
            }

            // 4. 策略 B：从根 ServiceProvider 解析 IBackgroundTaskQueue<CreateServerTask>
            if (targetQueue == null && rootServiceProvider != null && queueInterfaceType != null)
            {
                try
                {
                    targetQueue = rootServiceProvider.GetService(queueInterfaceType);
                    if (targetQueue != null)
                    {
                        _logger.LogInformation("[AiService] 成功从根 ServiceProvider 解析 IBackgroundTaskQueue<CreateServerTask>");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AiService] 从根 ServiceProvider 解析队列异常");
                }
            }

            // 5. 策略 C：从当前 serviceProvider 解析
            if (targetQueue == null && queueInterfaceType != null)
            {
                targetQueue = serviceProvider.GetService(queueInterfaceType);
            }

            if (targetQueue == null)
            {
                _logger.LogError("[AiService] 无法解析或定位 IBackgroundTaskQueue<CreateServerTask> 实例！");
                return false;
            }

            var method = targetQueue.GetType().GetMethod("QueueTaskAsync", new[] { typeof(CreateServerTask) })
                         ?? targetQueue.GetType().GetMethod("QueueTaskAsync");

            if (method == null)
            {
                _logger.LogError("[AiService] 未在任务队列中找到 QueueTaskAsync 方法！");
                return false;
            }

            var invokeResult = method.Invoke(targetQueue, new object[] { task });
            if (invokeResult is ValueTask valueTask)
            {
                await valueTask;
                _logger.LogInformation("[AiService] 成功通过 ValueTask 将任务写入后台队列: ServerId={ServerId}", task.ServerId);
                return true;
            }
            else if (invokeResult is Task taskObj)
            {
                await taskObj;
                _logger.LogInformation("[AiService] 成功通过 Task 将任务写入后台队列: ServerId={ServerId}", task.ServerId);
                return true;
            }
            else if (invokeResult != null)
            {
                var asTaskMethod = invokeResult.GetType().GetMethod("AsTask");
                if (asTaskMethod != null)
                {
                    var asTask = (Task)asTaskMethod.Invoke(invokeResult, null)!;
                    await asTask;
                    _logger.LogInformation("[AiService] 成功通过 AsTask() 将任务写入后台队列: ServerId={ServerId}", task.ServerId);
                    return true;
                }
                return true;
            }

            _logger.LogError("[AiService] QueueTaskAsync 执行后未返回可等待对象");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AiService] QueueCreateServerTaskAsync 异常: {Message}", ex.Message);
            return false;
        }
    }

    private async Task<(bool Success, string Message)> EnsureOnlineJavaAsync(string javaVersion, Func<string, double, Task>? onProgress = null)
    {
        try
        {
            string javaBaseDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Tools", "Java");
            string javaExec = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "java.exe" : "java";
            string existingJavaPath = Path.Combine(javaBaseDir, javaVersion, "bin", javaExec);
            if (File.Exists(existingJavaPath))
            {
                using var scope = _scopeFactory.CreateScope();
                var scanner = scope.ServiceProvider.GetService<IJavaScannerService>();
                if (scanner != null)
                {
                    await scanner.ScanJavaAsync(true);
                }
                return (true, $"Java {javaVersion} 已在系统内置目录中就绪！已自动刷新 Java 环境缓存。");
            }

            var deployerType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("MSLX.Daemon.Services.DeployServerService.ServerDeploymentService"))
                .FirstOrDefault(t => t != null);

            if (deployerType != null)
            {
                object? deployer = null;
                try
                {
                    var pluginManagerType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("MSLX.Daemon.Services.PluginsService.PluginManager"))
                        .FirstOrDefault(t => t != null);
                    if (pluginManagerType != null)
                    {
                        var pm = _serviceProvider.GetService(pluginManagerType);
                        if (pm != null)
                        {
                            var spField = pluginManagerType.GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic);
                            var rootSp = spField?.GetValue(pm) as IServiceProvider;
                            if (rootSp != null)
                            {
                                deployer = rootSp.GetService(deployerType);
                            }
                        }
                    }
                }
                catch { }

                deployer ??= _serviceProvider.GetService(deployerType);
                deployer ??= ActivatorUtilities.CreateInstance(_serviceProvider, deployerType);

                if (deployer != null)
                {
                    var ensureMethod = deployerType.GetMethod("EnsureJavaAsync");
                    if (ensureMethod != null)
                    {
                        var reportProgressType = deployerType.GetNestedType("ReportProgress");
                        Delegate? reportDelegate = null;
                        if (reportProgressType != null)
                        {
                            Func<string, double?, bool, Exception?, Task> reportFunc = async (msg, prog, isErr, ex) =>
                            {
                                _logger.LogInformation("[OnlineJava] {Message} ({Progress}%)", msg, prog);
                                if (onProgress != null)
                                {
                                    await onProgress(msg, prog ?? 0);
                                }
                            };
                            reportDelegate = Delegate.CreateDelegate(reportProgressType, reportFunc.Target, reportFunc.Method);
                        }

                        var taskResult = ensureMethod.Invoke(deployer, new object?[] { "", $"MSLX://Java/{javaVersion}", reportDelegate, CancellationToken.None });
                        if (taskResult is Task t)
                        {
                            await t;

                            // 部署成功后，强制全盘重新扫描 Java，确保立即加入有效缓存
                            using var scope = _scopeFactory.CreateScope();
                            var scanner = scope.ServiceProvider.GetService<IJavaScannerService>();
                            if (scanner != null)
                            {
                                await scanner.ScanJavaAsync(true);
                            }

                            return (true, $"MSLX 在线 Java {javaVersion} 已全自动下载、校验并部署安装成功！环境标识 `MSLX://Java/{javaVersion}` 已即刻就绪。");
                        }
                    }
                }
            }

            return (false, "无法定位或加载 MSLX 宿主部署服务组件。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EnsureOnlineJavaAsync 失败: {Message}", ex.Message);
            return (false, $"在线安装 Java {javaVersion} 异常: {ex.Message}");
        }
    }

    private static string? GetStringOrNull(JsonNode? node)
    {
        return node != null && node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : null;
    }

    private static bool IsWithinServerDirectory(string serverDirectory, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var directoryPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return string.Equals(path, root, comparison) || path.StartsWith(directoryPrefix, comparison);
    }

    private static void CopyDirectoryRecursively(string sourceDir, string targetDir, bool overwrite)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectoryRecursively(dir, dest, overwrite);
        }
    }
}
