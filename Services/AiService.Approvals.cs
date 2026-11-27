using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MSLX.Plugin.AiAgents.Utils;

namespace MSLX.Plugin.AiAgents.Services;

public partial class AiService
{
    private readonly object _approvalLock = new();
    private Dictionary<string, ToolApproval>? _approvals;
    private static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(30);
    private string ApprovalPath => Path.Combine(PluginData.DirectoryPath, "operations.json");

    private Dictionary<string, ToolApproval> LoadApprovalsLocked()
    {
        if (_approvals != null) return _approvals;
        var entries = File.Exists(ApprovalPath)
            ? JsonSerializer.Deserialize<Dictionary<string, ToolApproval>>(File.ReadAllText(ApprovalPath)) ?? new()
            : new();
        foreach (var operation in entries.Values)
        {
            if (operation.Status == "running") operation.Status = "unknown";
            if (operation.Status == "pending") operation.Status = "expired";
        }
        return _approvals = entries;
    }

    private void SaveApprovalsLocked() => PluginData.WriteJson(ApprovalPath, _approvals);

    private string CreatePendingOperation(string toolName, string argsJson, int serverId, string target, string action, string owner,
        int? requestedMinutes = null, string? reason = null)
    {
        var server = MSLX.SDK.MSLX.Config.Servers.GetServerList().FirstOrDefault(item => item.ID == serverId)
            ?? throw new InvalidOperationException("目标实例已不存在。");
        var args = JsonNode.Parse(argsJson)?.AsObject() ?? new JsonObject();
        args["server_id"] = serverId;
        args.Remove("confirmed");
        args.Remove("granted_minutes");
        args.Remove("_grant_owner");
        args.Remove("_approver");
        args.Remove("_requester_name");
        var now = DateTime.UtcNow;
        var operation = new ToolApproval
        {
            Id = Guid.NewGuid().ToString("N"),
            Tool = toolName,
            Owner = owner,
            ServerId = serverId,
            ServerName = server.Name,
            ServerDirectory = server.Base,
            BackupDirectory = server.BackupPath,
            Target = target,
            Action = action,
            RequestedMinutes = requestedMinutes,
            Reason = reason,
            CreatedAt = now,
            ExpiresAt = now + ApprovalLifetime,
            Arguments = args.ToJsonString()
        };
        lock (_approvalLock)
        {
            var entries = LoadApprovalsLocked();
            foreach (var old in entries.Values.Where(item => item.Status == "pending" && item.ExpiresAt <= now))
                _ = VisibleStatus(old);
            // Repeated submissions of the same pending change share an approval.
            var existing = entries.Values.FirstOrDefault(item => item.Status == "pending" &&
                item.ExpiresAt > now && item.Owner == owner && item.Tool == toolName &&
                item.ServerId == serverId && item.ServerDirectory == server.Base && item.Arguments == operation.Arguments);
            if (existing != null) return existing.Id;
            entries.Add(operation.Id, operation);
            try { SaveApprovalsLocked(); }
            catch { entries.Remove(operation.Id); throw; }
        }
        return operation.Id;
    }

    private async Task<string> RequestReplacementAsync(string tool, string args, int serverId, string path, string owner,
        Func<string, object, Task> onTool)
    {
        var confirmationId = CreatePendingOperation(tool, args, serverId, path, "edit", owner);
        await onTool(tool, new { serverId, filePath = path, action = "edit", requiresConfirmation = true, confirmationId });
        return PendingMessage(confirmationId);
    }

    private string? GetLocalApprovalStatus(string id)
    {
        lock (_approvalLock)
            return LoadApprovalsLocked().TryGetValue(id, out var operation) ? VisibleStatus(operation) : null;
    }

    private static string PendingMessage(string id) =>
        $"[PENDING_CONFIRMATION] 操作编号 {id}。请管理员在当前节点的 AI 助手中确认，外部 Agent 请打开 MCP 页的「待确认操作」。确认后系统自动执行，请使用 query_operation_status 查询，无需重发。";

    private string VisibleStatus(ToolApproval operation)
    {
        if (operation.Status != "pending") return operation.Status;
        if (operation.ExpiresAt <= DateTime.UtcNow ||
            (operation.Owner.StartsWith("mcp:", StringComparison.Ordinal) &&
             !_serviceProvider.GetRequiredService<McpTokenManager>().IsActive(operation.Owner[4..])))
        {
            operation.Arguments = null;
            operation.Status = "expired";
            return "expired";
        }
        return "pending";
    }

    private object DescribeApproval(ToolApproval operation) => new
    {
        id = operation.Id,
        tool = operation.Tool,
        serverId = operation.ServerId,
        serverName = operation.ServerName,
        target = operation.Target,
        action = operation.Action,
        requestedMinutes = operation.RequestedMinutes,
        reason = operation.Reason,
        source = operation.Owner.StartsWith("mcp:", StringComparison.Ordinal) ? "mcp" : "chat",
        requester = operation.Owner.StartsWith("mcp:", StringComparison.Ordinal)
            ? _serviceProvider.GetRequiredService<McpTokenManager>().GetName(operation.Owner[4..]) ?? "已删除的 MCP 令牌"
            : operation.Owner,
        approver = operation.Approver,
        status = VisibleStatus(operation),
        createdAt = operation.CreatedAt,
        expiresAt = operation.ExpiresAt
    };

    public IReadOnlyList<object> ListApprovals(string userId)
    {
        lock (_approvalLock)
        {
            return LoadApprovalsLocked().Values
                .Where(item => item.Owner == userId || item.Owner.StartsWith("mcp:", StringComparison.Ordinal))
                .OrderByDescending(item => VisibleStatus(item) == "pending")
                .ThenByDescending(item => item.CreatedAt)
                .Take(100).Select(DescribeApproval).ToArray();
        }
    }

    internal object? GetMcpApproval(string id, string tokenId)
    {
        lock (_approvalLock)
            return LoadApprovalsLocked().TryGetValue(id, out var item) && item.Owner == $"mcp:{tokenId}"
                ? DescribeApproval(item) : null;
    }

    public async Task<(bool Success, string Message, object? Data)> ConfirmPendingToolAsync(
        string confirmationId, bool approved, string userId, int? grantedMinutes = null)
    {
        ToolApproval operation;
        string arguments;
        lock (_approvalLock)
        {
            if (string.IsNullOrEmpty(userId) || !LoadApprovalsLocked().TryGetValue(confirmationId, out var item) ||
                (item.Owner != userId && !item.Owner.StartsWith("mcp:", StringComparison.Ordinal)) ||
                VisibleStatus(item) != "pending" || item.Arguments == null)
                return (false, "操作不存在、已处理或已过期。请刷新列表。", null);

            if (approved)
            {
                var server = MSLX.SDK.MSLX.Config.Servers.GetServerList().FirstOrDefault(s => s.ID == item.ServerId);
                if (server == null || server.Name != item.ServerName || server.Base != item.ServerDirectory ||
                    ((item.Tool is "delete_server_backup" or "restore_server_backup") && server.BackupPath != item.BackupDirectory))
                    return (false, "目标实例已变化，请重新发起操作。", null);
                if (item.Owner.StartsWith("mcp:", StringComparison.Ordinal) &&
                    !_serviceProvider.GetRequiredService<McpTokenManager>().CanApprove(item.Owner[4..], item.ServerId))
                    return (false, "原令牌已失效或已无权操作该实例。", null);
            }

            operation = item;
            arguments = item.Arguments;
            operation.Approver = userId;
            operation.Status = approved ? "running" : "rejected";
            try { SaveApprovalsLocked(); }
            catch { operation.Approver = null; operation.Status = "pending"; throw; }
            operation.Arguments = null;
            if (!approved) return (true, "已拒绝，操作已取消。", DescribeApproval(operation));
        }

        if (operation.Tool == "request_terminal_permission")
        {
            var argsObj = JsonNode.Parse(arguments)?.AsObject() ?? new JsonObject();
            var requesterName = operation.Owner.StartsWith("mcp:", StringComparison.Ordinal)
                ? _serviceProvider.GetRequiredService<McpTokenManager>().GetName(operation.Owner[4..]) ?? "已删除的 MCP 令牌"
                : operation.Owner;
            argsObj["_grant_owner"] = operation.Owner;
            argsObj["_approver"] = userId;
            argsObj["_requester_name"] = requesterName;
            if (grantedMinutes is > 0)
                argsObj["granted_minutes"] = grantedMinutes.Value;
            arguments = argsObj.ToJsonString();
        }

        // The HTTP client can leave while the approved operation is running.
        var result = await ExecuteToolAsync(operation.Tool, arguments, (_, _) => Task.CompletedTask,
            assumeConfirmed: true, userId: userId);
        lock (_approvalLock)
        {
            var success = !result.StartsWith("[ERROR]", StringComparison.Ordinal);
            operation.Status = success ? "succeeded" : "failed";
            try { SaveApprovalsLocked(); }
            catch (IOException ex)
            {
                _logger.LogError(ex, "无法保存审批 {Id} 的执行结果", operation.Id);
                operation.Status = "unknown";
                return (false, "操作结果无法保存，请核对实例状态。", DescribeApproval(operation));
            }
            return (success, result, DescribeApproval(operation));
        }
    }

    private sealed class ToolApproval
    {
        public required string Id { get; init; }
        public required string Tool { get; init; }
        public required string Owner { get; init; }
        public required int ServerId { get; init; }
        public required string ServerName { get; init; }
        public string? ServerDirectory { get; init; }
        public string? BackupDirectory { get; init; }
        public required string Target { get; init; }
        public required string Action { get; init; }
        public int? RequestedMinutes { get; init; }
        public string? Reason { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public string Status { get; set; } = "pending";
        public string? Approver { get; set; }
        [JsonIgnore]
        public string? Arguments { get; set; }
    }
}
