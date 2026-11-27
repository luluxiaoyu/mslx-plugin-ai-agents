using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MSLX.Plugin.AiAgents.Services;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MSLX.Plugin.AiAgents.Hubs;

[Authorize(Roles = "admin")]
public class AiChatHub : Hub
{
    private readonly AiService _aiService;
    private readonly ILogger<AiChatHub> _logger;

    public AiChatHub(AiService aiService, ILogger<AiChatHub> logger)
    {
        _aiService = aiService;
        _logger = logger;
    }

    public Task SendMessage(JsonArray messages) => StartChatTurn(Guid.NewGuid().ToString("N"), messages);

    public async Task StartChatTurn(string turnId, JsonArray messages)
    {
        var userId = RequireCurrentAdministrator();
        _logger.LogInformation("收到 SignalR AI 对话请求，ConnectionId: {ConnectionId}, UserId: {UserId}", Context.ConnectionId, userId);

        try
        {
            await _aiService.RunChatTurnAsync(
                userId, turnId, messages,
                async (chunk) =>
                {
                    await Clients.Caller.SendAsync("ChatChunk", chunk, turnId);
                },
                async (toolName, toolData) =>
                {
                    await Clients.Caller.SendAsync("ToolExecuted", toolName, toolData, turnId);
                },
                Context.ConnectionAborted,
                async (contextStatus) =>
                {
                    await Clients.Caller.SendAsync("ContextStatus", contextStatus, turnId);
                }
            );

            await Clients.Caller.SendAsync("ChatComplete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SignalR AI 对话处理异常");
            try
            {
                await Clients.Caller.SendAsync("ChatError", ex.Message, turnId);
            }
            catch (Exception sendEx)
            {
                _logger.LogWarning(sendEx, "向已断开的 SignalR 客户端发送 ChatError 失败");
            }
        }
    }

    public async Task<object> ConfirmToolAction(string confirmationId, bool approved)
    {
        var userId = RequireCurrentAdministrator();

        try
        {
            var (success, message, data) = await _aiService.ConfirmPendingToolAsync(confirmationId, approved, userId);

            return new { success, message, data };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SignalR AI 敏感操作确认异常");
            return new { success = false, message = ex.Message, data = (object?)null };
        }
    }

    private string RequireCurrentAdministrator()
    {
        var user = Context.User;
        var userId = user?.FindFirst("UserId")?.Value;
        // The host revalidates delegated node connections before dispatching hub calls.
        if (!string.IsNullOrEmpty(userId) && user?.IsInRole("admin") == true &&
            (user.Identity?.AuthenticationType == "NodeAuth" ||
             MSLX.SDK.MSLX.Config.Users.GetUserById(userId)?.Role == "admin"))
        {
            return userId;
        }

        throw new HubException("当前账号没有管理员权限，请重新登录。");
    }
}
