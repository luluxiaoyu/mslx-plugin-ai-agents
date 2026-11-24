using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MSLX.Plugin.AiAgents.Models;
using MSLX.Plugin.AiAgents.Services;
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

    public AiSettingsController(McpTokenManager tokenManager)
    {
        _tokenManager = tokenManager;
    }

    [HttpGet]
    [Authorize(Roles = "admin")]
    public IActionResult GetAiSettings()
    {
        var config = Config.ReadConfig();
        return Ok(new ApiResponse<object>
        {
            Code = 200,
            Message = "获取成功",
            Data = new
            {
                AiEnabled = (bool?)(config["aiEnabled"]) ?? false,
                AiApiKey = (string?)(config["aiApiKey"]) ?? "",
                AiBaseUrl = (string?)(config["aiBaseUrl"]) ?? "https://api.deepseek.com/v1",
                AiModelName = (string?)(config["aiModelName"]) ?? "deepseek-chat"
            }
        });
    }

    [HttpPost]
    [Authorize(Roles = "admin")]
    public IActionResult UpdateAiSettings([FromBody] AiSettingsRequest request)
    {
        Config.WriteConfigKey("aiEnabled", request.AiEnabled);
        Config.WriteConfigKey("aiApiKey", request.AiApiKey ?? "");
        Config.WriteConfigKey("aiBaseUrl", request.AiBaseUrl ?? "https://api.deepseek.com/v1");
        Config.WriteConfigKey("aiModelName", request.AiModelName ?? "deepseek-chat");

        return Ok(new ApiResponse<object>
        {
            Code = 200,
            Message = "AI 配置更新成功"
        });
    }

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

        var created = _tokenManager.GenerateToken(request.Name, request.GetEffectiveDays());
        return Ok(new ApiResponse<McpTokenCreatedDto>
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

        bool success = permanent
            ? _tokenManager.DeleteToken(id)
            : _tokenManager.RevokeToken(id);

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
}
