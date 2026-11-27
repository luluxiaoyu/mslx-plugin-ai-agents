using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MSLX.Plugin.AiAgents.Services;

public partial class AiService
{
    private void RegisterTerminalTools(JsonArray tools)
    {
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "request_terminal_permission",
                ["description"] = "当 MCP 模式未开启「默认开启终端权限」时，向管理员申请指定 Minecraft 服务器实例的临时命令行/终端使用权限。必须指定使用时长（小时/分钟），提交后由管理员在 MCP 面板审批，审批通过后在有效期内即可直接调用 run_server_terminal。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "要申请终端权限的服务器实例 ID" },
                        ["duration_hours"] = new JsonObject { ["type"] = "integer", ["description"] = "申请使用的小时数（0-168，可与 duration_minutes 组合使用）" },
                        ["duration_minutes"] = new JsonObject { ["type"] = "integer", ["description"] = "申请使用的分钟数（0-1440，与 duration_hours 之和至少为 1 分钟，最长不超过 7 天）" },
                        ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "申请原因或计划执行的命令行操作说明（将展示给管理员审批）" }
                    }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "run_server_terminal",
                ["description"] = "在指定 Minecraft 服务器实例的根目录（或指定相对子目录）内拉起命令行执行终端指令（自动切换到目标实例目录；Windows 默认使用 PowerShell，macOS 默认使用 zsh，Linux 默认使用 bash）。MCP 模式下若未开启「默认开启终端权限」，需先通过 request_terminal_permission 申请临时权限并经管理员在 MCP 面板审批通过。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "目标服务器实例 ID，未指定时使用最新服务器" },
                        ["command"] = new JsonObject { ["type"] = "string", ["description"] = "要在实例目录内执行的 Shell / PowerShell 命令" },
                        ["shell"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "终端类型：'auto'（默认，Windows 用 PowerShell，macOS 用 zsh，Linux 用 bash）、'powershell'、'pwsh'、'cmd'、'zsh'、'bash'、'sh'",
                            ["enum"] = new JsonArray { "auto", "powershell", "pwsh", "cmd", "zsh", "bash", "sh" }
                        },
                        ["working_dir"] = new JsonObject { ["type"] = "string", ["description"] = "可选：相对于该服务器实例根目录的子目录路径，如 'mods'、'plugins'；留空则直接在实例根目录执行" },
                        ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "命令执行超时秒数（1-300，默认 30 秒）" }
                    },
                    ["required"] = new JsonArray { "command" }
                }
            }
        });
    }

    private async Task<string?> HandleTerminalToolAsync(
        string name,
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        bool assumeConfirmed,
        string userId)
    {
        switch (name)
        {
            case "request_terminal_permission":
                return await HandleRequestTerminalPermissionAsync(args, onToolExecuted, assumeConfirmed, userId);

            case "run_server_terminal":
                return await HandleRunServerTerminalAsync(args, onToolExecuted, assumeConfirmed, userId);

            default:
                return null;
        }
    }

    private async Task<string> HandleRequestTerminalPermissionAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        bool assumeConfirmed,
        string userId)
    {
        var (targetServer, error) = ResolveTargetServer(args);
        if (targetServer == null) return error ?? "[ERROR] 未找到服务器实例。";

        bool isMcp = userId.StartsWith("mcp:", StringComparison.Ordinal);
        if (!assumeConfirmed && isMcp && _terminalService.IsMcpTerminalEnabledByDefault())
        {
            await onToolExecuted("request_terminal_permission", new
            {
                serverId = targetServer.ID,
                serverName = targetServer.Name,
                alreadyEnabledByDefault = true
            });
            return $"[SUCCESS] 当前 MCP 已开启「默认开启终端权限」，无需额外申请，可直接对实例 #{targetServer.ID} ({targetServer.Name}) 调用 run_server_terminal。";
        }

        int hours = ParseNonNegativeInt(args?["duration_hours"]);
        int minutes = ParseNonNegativeInt(args?["duration_minutes"]);
        if (hours < 0 || minutes < 0)
            return "[ERROR] 使用时长不能为负数，请检查 duration_hours 和 duration_minutes。";

        long totalLong = (long)hours * 60 + minutes;
        if (totalLong <= 0)
            return "[ERROR] 请指定有效的使用时长（duration_hours 或 duration_minutes，合计至少 1 分钟）。";
        if (totalLong > ServerTerminalService.MaxGrantMinutes)
            return $"[ERROR] 单次申请的终端权限时长不能超过 7 天（{ServerTerminalService.MaxGrantMinutes} 分钟）。";

        int totalMinutes = (int)totalLong;
        string reason = args?["reason"]?.ToString()?.Trim() ?? "";

        if (!assumeConfirmed)
        {
            var normalizedArgs = args != null ? JsonNode.Parse(args.ToJsonString())!.AsObject() : new JsonObject();
            normalizedArgs["server_id"] = targetServer.ID;
            normalizedArgs["duration_hours"] = hours;
            normalizedArgs["duration_minutes"] = minutes;
            if (!string.IsNullOrEmpty(reason)) normalizedArgs["reason"] = reason;

            string durationText = ServerTerminalService.FormatDuration(totalMinutes);
            string targetSummary = $"实例终端权限（{durationText}）";
            string confirmationId = CreatePendingOperation(
                "request_terminal_permission",
                normalizedArgs.ToJsonString(),
                targetServer.ID,
                targetSummary,
                "grant_terminal",
                userId,
                requestedMinutes: totalMinutes,
                reason: string.IsNullOrEmpty(reason) ? null : reason);

            await onToolExecuted("request_terminal_permission", new
            {
                serverId = targetServer.ID,
                serverName = targetServer.Name,
                durationHours = hours,
                durationMinutes = minutes,
                totalMinutes,
                durationText,
                reason,
                action = "grant_terminal",
                requiresConfirmation = true,
                confirmationId
            });

            return $"[PENDING_CONFIRMATION] 操作编号 {confirmationId}。已提交实例 #{targetServer.ID} ({targetServer.Name}) 的终端权限申请（时长：{durationText}）。" +
                   (isMcp
                       ? "请管理员在 MCP 面板的「待确认操作」审批通过，外部 Agent 可通过 query_operation_status 查询审批结果。"
                       : "请在下方操作卡片确认授权后生效，无需重发。");
        }

        // 已获批准（通过 ConfirmPendingOperationWithGrantAsync 调用时，granted_minutes 可能已被管理员调整）
        int grantedMinutes = ParsePositiveIntOrDefault(args?["granted_minutes"], totalMinutes);
        grantedMinutes = Math.Clamp(grantedMinutes, 1, ServerTerminalService.MaxGrantMinutes);
        string requesterName = args?["_requester_name"]?.ToString() ?? (isMcp ? "MCP 令牌" : userId);
        string grantOwner = args?["_grant_owner"]?.ToString() ?? userId;
        string? approver = args?["_approver"]?.ToString();

        var grant = _terminalService.GrantTemporaryAccess(
            owner: grantOwner,
            requesterName: requesterName,
            serverId: targetServer.ID,
            serverName: targetServer.Name,
            grantedMinutes: grantedMinutes,
            requestedHours: hours,
            requestedMinutes: minutes,
            reason: string.IsNullOrEmpty(reason) ? null : reason,
            approver: approver);

        var grantText = ServerTerminalService.FormatDuration(grant.GrantedMinutes);
        await onToolExecuted("request_terminal_permission", new
        {
            serverId = targetServer.ID,
            serverName = targetServer.Name,
            grantId = grant.Id,
            grantedMinutes = grant.GrantedMinutes,
            expiresAt = grant.ExpiresAt,
            success = true
        });

        return $"[SUCCESS] 已批准对实例 #{targetServer.ID} ({targetServer.Name}) 的临时终端权限，授权时长 {grantText}，有效期至 {grant.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}。";
    }

    private async Task<string> HandleRunServerTerminalAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        bool assumeConfirmed,
        string userId)
    {
        var (targetServer, error) = ResolveTargetServer(args);
        if (targetServer == null) return error ?? "[ERROR] 未找到服务器实例。";

        string command = args?["command"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(command))
            return "[ERROR] command 不能为空。";

        string requestedShell = args?["shell"]?.ToString()?.Trim() ?? "auto";
        string relWorkingDir = args?["working_dir"]?.ToString()?.Trim() ?? "";
        int timeoutSeconds = ParsePositiveIntOrDefault(args?["timeout_seconds"], ServerTerminalService.DefaultTimeoutSeconds);

        string safeBaseDir = GetServerDirectory(targetServer);
        Directory.CreateDirectory(safeBaseDir);

        string workingDir = string.IsNullOrEmpty(relWorkingDir)
            ? safeBaseDir
            : Path.GetFullPath(Path.Combine(safeBaseDir, relWorkingDir));

        if (!IsWithinServerDirectory(safeBaseDir, workingDir))
            return "[ERROR] 禁止越权：working_dir 必须位于目标服务器实例目录内。";
        if (!Directory.Exists(workingDir))
            return $"[ERROR] 指定的工作目录不存在: {(string.IsNullOrEmpty(relWorkingDir) ? safeBaseDir : relWorkingDir)}";

        bool isMcp = userId.StartsWith("mcp:", StringComparison.Ordinal);
        ServerTerminalService.TerminalPermissionGrant? activeGrant = null;

        if (!assumeConfirmed)
        {
            if (isMcp)
            {
                bool allowedByDefault = _terminalService.IsMcpTerminalEnabledByDefault();
                if (!allowedByDefault && !_terminalService.HasActiveGrant(userId, targetServer.ID, out activeGrant))
                {
                    return $"[ERROR] 当前 MCP 未开启「默认开启终端权限」，且令牌尚未获得实例 #{targetServer.ID} ({targetServer.Name}) 的临时终端授权（或授权已过期）。请先调用 request_terminal_permission(server_id={targetServer.ID}, duration_hours=..., duration_minutes=..., reason=\"...\") 向管理员申请权限，待管理员在 MCP 面板审批后再执行命令。";
                }
            }
            else
            {
                bool autoApprove = _terminalService.IsApiKeyTerminalAutoApprove();
                if (!autoApprove && !_terminalService.HasActiveGrant(userId, targetServer.ID, out activeGrant))
                {
                    string displayRelDir = Path.GetRelativePath(safeBaseDir, workingDir).Replace('\\', '/');
                    if (displayRelDir == ".") displayRelDir = "/";
                    string preview = command.Length > 120 ? command[..120] + "…" : command;
                    string confirmationId = CreatePendingOperation(
                        "run_server_terminal",
                        args?.ToJsonString() ?? "{}",
                        targetServer.ID,
                        preview,
                        "run_terminal",
                        userId);

                    await onToolExecuted("run_server_terminal", new
                    {
                        serverId = targetServer.ID,
                        serverName = targetServer.Name,
                        command,
                        shell = requestedShell,
                        workingDir = displayRelDir,
                        action = "run_terminal",
                        requiresConfirmation = true,
                        confirmationId
                    });

                    return PendingMessage(confirmationId);
                }
            }
        }

        try
        {
            var exec = await _terminalService.ExecuteCommandAsync(
                workingDirectory: workingDir,
                command: command,
                requestedShell: requestedShell,
                timeoutSeconds: timeoutSeconds,
                serverId: targetServer.ID,
                serverName: targetServer.Name,
                serverBaseDir: safeBaseDir);

            string displayRelDir = Path.GetRelativePath(safeBaseDir, exec.WorkingDirectory).Replace('\\', '/');
            if (displayRelDir == ".") displayRelDir = "/";

            var payload = new
            {
                serverId = targetServer.ID,
                serverName = targetServer.Name,
                shell = exec.ShellUsed,
                workingDir = displayRelDir,
                workingDirectory = exec.WorkingDirectory,
                exitCode = exec.ExitCode,
                timedOut = exec.TimedOut,
                durationMs = exec.DurationMs,
                stdout = exec.Stdout,
                stderr = exec.Stderr,
                stdoutTruncated = exec.StdoutTruncated,
                stderrTruncated = exec.StderrTruncated,
                grantExpiresAt = activeGrant?.ExpiresAt,
                success = !exec.TimedOut && exec.ExitCode == 0
            };

            await onToolExecuted("run_server_terminal", payload);

            var header = exec.TimedOut
                ? $"[ERROR] 命令执行超时（超过 {Math.Clamp(timeoutSeconds, 1, ServerTerminalService.MaxTimeoutSeconds)} 秒已强制终止）"
                : exec.ExitCode == 0
                    ? $"[SUCCESS] 已在实例 #{targetServer.ID} ({targetServer.Name}) 目录执行命令（退出码 0，耗时 {exec.DurationMs} ms，Shell: {exec.ShellUsed}）"
                    : $"[SUCCESS] 命令已在实例 #{targetServer.ID} ({targetServer.Name}) 目录执行完毕（退出码 {exec.ExitCode}，耗时 {exec.DurationMs} ms，Shell: {exec.ShellUsed}）";

            return $"{header}\n工作目录: {exec.WorkingDirectory}\n{JsonSerializer.Serialize(payload)}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DirectoryNotFoundException)
        {
            return $"[ERROR] {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "在实例 #{ServerId} 执行终端命令失败", targetServer.ID);
            return $"[ERROR] 执行终端命令失败: {ex.Message}";
        }
    }

    private static int ParseNonNegativeInt(JsonNode? node)
    {
        if (node == null) return 0;
        if (int.TryParse(node.ToString(), out var value)) return value;
        if (decimal.TryParse(node.ToString(), out var dec) && decimal.Truncate(dec) == dec)
            return (int)dec;
        return -1;
    }

    private static int ParsePositiveIntOrDefault(JsonNode? node, int fallback)
    {
        if (node == null) return fallback;
        return int.TryParse(node.ToString(), out var value) && value > 0 ? value : fallback;
    }
}
