using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MSLX.Plugin.AiAgents.Services;
using MSLX.SDK.Models;

namespace MSLX.Plugin.AiAgents.Controllers;

[ApiController]
[Route("api/plugin/mslx-plugin-ai-agents/ai")]
[Authorize(Roles = "admin")]
public class AiAssistantController : ControllerBase
{
    private readonly AiService _aiService;

    public AiAssistantController(AiService aiService)
    {
        _aiService = aiService;
    }

    [HttpPost("chat")]
    [Authorize(Roles = "admin")]
    public async Task Chat([FromBody] ChatRequest request)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        if (request.Messages == null || request.Messages.Count == 0)
        {
            await SendSseEventAsync("message", "请输入有效的对话内容。");
            await SendSseEventAsync("done", "[DONE]");
            return;
        }

        try
        {
            var userId = User?.FindFirst("UserId")?.Value ?? "";
            var jsonMessages = new JsonArray();
            foreach (var m in request.Messages)
            {
                jsonMessages.Add(new JsonObject
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content
                });
            }

            await _aiService.RunChatTurnAsync(
                userId, request.TurnId ?? Guid.NewGuid().ToString("N"), jsonMessages,
                async (chunkText) =>
                {
                    await SendSseEventAsync("message", chunkText);
                },
                async (toolName, toolData) =>
                {
                    var toolPayload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        tool = toolName,
                        data = toolData
                    });
                    await SendSseEventAsync("tool_executed", toolPayload);
                },
                HttpContext.RequestAborted,
                async (contextStatus) =>
                {
                    var payload = System.Text.Json.JsonSerializer.Serialize(contextStatus, new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                    });
                    await SendSseEventAsync("context_status", payload);
                }
            );
        }
        catch (Exception ex)
        {
            await SendSseEventAsync("error", ex.Message);
        }
        finally
        {
            await SendSseEventAsync("done", "[DONE]");
        }
    }

    private async Task SendSseEventAsync(string eventType, string data)
    {
        try
        {
            var formattedData = data.Replace("\r", "").Replace("\n", "\\n");
            await Response.WriteAsync($"event: {eventType}\ndata: {formattedData}\n\n");
            await Response.Body.FlushAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SSE 写入失败 (客户端可能已断开): {ex.Message}");
        }
    }

    [HttpGet("turns/{id}")]
    public IActionResult GetTurn(string id) => Ok(_aiService.GetTurnStatus(UserId, id));

    [HttpPost("turns/{id}/stop")]
    public IActionResult StopTurn(string id) => Ok(new { accepted = _aiService.StopTurn(UserId, id) });

    [HttpGet("operations")]
    public IActionResult ListOperations() => Ok(_aiService.ListApprovals(UserId));

    private string UserId => User.FindFirst("UserId")?.Value ?? "";

    [HttpPost("confirm-tool")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> ConfirmTool([FromBody] ConfirmToolRequest request)
    {
        var (success, message, data) = await _aiService.ConfirmPendingToolAsync(
            request.ConfirmationId, request.Approved, UserId, request.GrantedMinutes);
        return Ok(new { success, message, data });
    }
}

public class ConfirmToolRequest
{
    public string ConfirmationId { get; set; } = "";
    public bool Approved { get; set; }

    [Range(1, ServerTerminalService.MaxGrantMinutes, ErrorMessage = "授权时长需在 1 分钟至 10080 分钟（7 天）之间。")]
    public int? GrantedMinutes { get; set; }
}

public class ChatRequest
{
    public List<ChatMessageDto>? Messages { get; set; }
    public string? TurnId { get; set; }
}

public class ChatMessageDto
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}
