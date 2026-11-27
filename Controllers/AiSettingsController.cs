using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MSLX.Plugin.AiAgents.Models;
using MSLX.Plugin.AiAgents.Services;
using MSLX.Plugin.AiAgents.Utils;
using MSLX.SDK;
using MSLX.SDK.Interfaces;
using MSLX.SDK.Models;

namespace MSLX.Plugin.AiAgents.Controllers;

[ApiController]
[Route("api/plugin/mslx-plugin-ai-agents/settings")]
public class AiSettingsController : ControllerBase
{
    private static IPluginConfigBridge Config =>
        MSLX.SDK.MSLX.Config.GetPluginConfig("mslx-plugin-ai-agents");

    private readonly McpTokenManager _tokenManager;
    private readonly AiService _aiService;

    public AiSettingsController(McpTokenManager tokenManager, AiService aiService)
    {
        _tokenManager = tokenManager;
        _aiService = aiService;
    }

    [HttpGet]
    [Authorize(Roles = "admin")]
    public IActionResult GetAiSettings()
    {
        var config = Config.ReadConfig();
        var (contextTokens, contextLabel) = ModelContextLengthHelper.Resolve((string?)config["aiContextLength"]);
        return Ok(new ApiResponse<object>
        {
            Code = 200,
            Message = "获取成功",
            Data = new
            {
                AiEnabled = !string.IsNullOrWhiteSpace((string?)config["aiApiKey"]),
                AiApiKey = (string?)(config["aiApiKey"]) ?? "",
                AiBaseUrl = (string?)(config["aiBaseUrl"]) ?? "https://api.deepseek.com/v1",
                AiModelName = (string?)(config["aiModelName"]) ?? "deepseek-chat",
                AiContextLength = contextLabel,
                AiContextTokens = contextTokens,
                BasePromptTokens = _aiService.GetBaseContextTokens()
            }
        });
    }

    [HttpPost]
    [Authorize(Roles = "admin")]
    public IActionResult UpdateAiSettings([FromBody] AiSettingsRequest request)
    {
        ModelContextLengthHelper.TryParse(request.AiContextLength, out var contextTokens, out var normalizedContextLength, out _);

        Config.WriteConfigKey("aiEnabled", !string.IsNullOrWhiteSpace(request.AiApiKey));
        Config.WriteConfigKey("aiApiKey", request.AiApiKey ?? "");
        Config.WriteConfigKey("aiBaseUrl", request.AiBaseUrl ?? "https://api.deepseek.com/v1");
        Config.WriteConfigKey("aiModelName", request.AiModelName ?? "deepseek-chat");
        Config.WriteConfigKey("aiContextLength", normalizedContextLength);

        return Ok(new ApiResponse<object>
        {
            Code = 200,
            Message = "AI 配置更新成功",
            Data = new
            {
                AiContextLength = normalizedContextLength,
                AiContextTokens = contextTokens,
                BasePromptTokens = _aiService.GetBaseContextTokens()
            }
        });
    }

    [HttpPost("test-model")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> TestModel([FromBody] AiSettingsRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AiApiKey) ||
            !Uri.TryCreate((request.AiBaseUrl ?? "").TrimEnd('/') + "/chat/completions", UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https"))
            return BadRequest(new { message = "请检查 API Key 和模型服务地址。" });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = new HttpClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, url);
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.AiApiKey.Trim());
        message.Content = System.Net.Http.Json.JsonContent.Create(new
        {
            model = request.AiModelName,
            messages = new[] { new { role = "user", content = "Reply OK." } },
            stream = false
        });
        try
        {
            using var response = await client.SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return BadRequest(new { message = $"模型服务返回 HTTP {(int)response.StatusCode}，请检查模型配置。" });
            using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != System.Text.Json.JsonValueKind.Array || choices.GetArrayLength() == 0)
                return BadRequest(new { message = "服务响应不是可用的聊天补全结果。" });
            return Ok(new { message = "模型连接成功。" });
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return BadRequest(new { message = "模型服务无法连接或响应无效，请检查地址和网络。" });
        }
    }

    [HttpGet("mcp/instances")]
    [Authorize(Roles = "admin")]
    public IActionResult GetInstances() => Ok(MSLX.SDK.MSLX.Config.Servers.GetServerList()
        .Select(server => new { id = server.ID, name = server.Name }).ToArray());

    [HttpGet("mcp/tokens")]
    [Authorize(Roles = "admin")]
    public IActionResult GetMcpTokens()
    {
        var tokens = _tokenManager.ListTokens();
        return Ok(new ApiResponse<List<McpTokenDto>>
        {
            Code = 200,
            Message = "获取成功",
            Data = tokens
        });
    }

    [HttpPost("mcp/tokens")]
    [Authorize(Roles = "admin")]
    public IActionResult CreateMcpToken([FromBody] CreateMcpTokenRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new ApiResponse<object>
            {
                Code = 400,
                Message = "令牌名称不能为空"
            });
        }

        var days = request.GetEffectiveDays();
        if (days is < 0 or > 36500)
            return BadRequest(new { message = "令牌有效期无效。" });
        if (request.ServerIds != null)
        {
            var available = MSLX.SDK.MSLX.Config.Servers.GetServerList().Select(server => server.ID).ToHashSet();
            if (request.ServerIds.Length == 0 || request.ServerIds.Any(id => !available.Contains(id)))
                return BadRequest(new { message = "请选择当前节点上的有效实例。" });
        }

        var created = _tokenManager.GenerateToken(request.Name, days, request.ReadOnly, request.ServerIds);
        return Ok(new ApiResponse<McpTokenDto>
        {
            Code = 200,
            Message = "令牌创建成功",
            Data = created
        });
    }

    [HttpDelete("mcp/tokens/{id}")]
    [Authorize(Roles = "admin")]
    public IActionResult DeleteMcpToken([FromRoute] string id, [FromQuery] bool permanent = false)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new ApiResponse<object>
            {
                Code = 400,
                Message = "令牌 ID 不能为空"
            });
        }

        bool success;
        try { success = permanent ? _tokenManager.DeleteToken(id) : _tokenManager.RevokeToken(id); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (IOException) { return StatusCode(500, new { message = "无法保存令牌变更，请检查插件数据目录。" }); }

        if (!success)
        {
            return NotFound(new ApiResponse<object>
            {
                Code = 404,
                Message = "未找到指定令牌"
            });
        }

        return Ok(new ApiResponse<object>
        {
            Code = 200,
            Message = permanent ? "令牌已删除" : "令牌已撤销"
        });
    }

    [HttpPost("mcp/tokens/{id}/revoke")]
    [Authorize(Roles = "admin")]
    public IActionResult RevokeMcpToken([FromRoute] string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new ApiResponse<object>
            {
                Code = 400,
                Message = "令牌 ID 不能为空"
            });
        }

        bool success = _tokenManager.RevokeToken(id);
        if (!success)
        {
            return NotFound(new ApiResponse<object>
            {
                Code = 404,
                Message = "未找到指定令牌"
            });
        }

        return Ok(new ApiResponse<object>
        {
            Code = 200,
            Message = "令牌已撤销"
        });
    }
}

public class AiSettingsRequest
{
    public bool AiEnabled { get; set; }
    public string? AiApiKey { get; set; }
    public string? AiBaseUrl { get; set; }
    public string? AiModelName { get; set; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "模型上下文长度不能为空，支持如 128k、256k、1M。")]
    [ModelContextLength]
    public string AiContextLength { get; set; } = ModelContextLengthHelper.DefaultContextLengthText;
}
