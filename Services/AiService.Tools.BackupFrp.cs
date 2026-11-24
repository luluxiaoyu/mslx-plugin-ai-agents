using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MSLX.SDK;
using MSLX.SDK.IServices;
using MSLX.SDK.Models;
using MSLX.SDK.Models.Instance;
using Newtonsoft.Json.Linq;

namespace MSLX.Plugin.AiAgents.Services;

public partial class AiService
{
    private void RegisterBackupFrpTools(JsonArray tools)
    {
        // 1. create_server_backup
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "create_server_backup",
                ["description"] = "触发 Minecraft 服务器实例的全量备份任务。系统将安全通知服务器保存世界存档并自动压缩打包为 zip 存入备份目录。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合当前用户界面上下文的主机 ID 或最新服务器" }
                    }
                }
            }
        });

        // 2. list_server_backups
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "list_server_backups",
                ["description"] = "列出指定服务器实例（或全部服务器）的历史备份文件列表，包含文件名、文件大小、创建时间和备份存储路径。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合当前用户界面上下文的主机 ID 或列出全部实例备份" }
                    }
                }
            }
        });

        // 3. restore_server_backup
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "restore_server_backup",
                ["description"] = "从指定的备份 zip 文件还原 Minecraft 服务器存档与配置。高风险操作，还原会覆盖现有文件，需要二次确认且服务器必须处于停止状态。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "目标服务器 ID" },
                        ["file_name"] = new JsonObject { ["type"] = "string", ["description"] = "要还原的备份文件名 (例如 'mslx-backup_20261123_120000.zip')" },
                        ["confirmed"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否已确认授权执行还原覆盖操作" }
                    },
                    ["required"] = new JsonArray { "server_id", "file_name" }
                }
            }
        });

        // 4. delete_server_backup
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "delete_server_backup",
                ["description"] = "删除指定的服务器备份文件以释放磁盘存储空间。高风险操作，不可撤销，需要二次确认。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "目标服务器 ID" },
                        ["file_name"] = new JsonObject { ["type"] = "string", ["description"] = "要删除的备份文件名 (例如 'mslx-backup_20261123_120000.zip')" },
                        ["confirmed"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否已确认授权执行删除操作" }
                    },
                    ["required"] = new JsonArray { "server_id", "file_name" }
                }
            }
        });

        // 5. query_frp_status
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "query_frp_status",
                ["description"] = "查询系统中配置的 FRP 内网穿透隧道列表及其运行状态，或查询指定 FRP 隧道的详细配置与近期运行日志。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["tunnel_id"] = new JsonObject { ["type"] = "number", ["description"] = "FRP 隧道 ID，若不指定则查询所有 FRP 隧道列表及运行状态" }
                    }
                }
            }
        });

        // 6. control_frp_tunnel
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "control_frp_tunnel",
                ["description"] = "控制 FRP 内网穿透隧道的启动、停止或获取实时运行日志。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["tunnel_id"] = new JsonObject { ["type"] = "number", ["description"] = "FRP 隧道 ID" },
                        ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "start", "stop", "log" }, ["description"] = "操作类型: 'start' (启动隧道), 'stop' (停止隧道), 'log' (获取近期日志)" }
                    },
                    ["required"] = new JsonArray { "tunnel_id", "action" }
                }
            }
        });

        // 7. manage_scheduled_tasks
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "manage_scheduled_tasks",
                ["description"] = "管理系统的计划任务（定时任务），支持查看任务列表、创建新计划任务、删除任务或启用/禁用任务。可用于定时重启服务器、定时备份、定时控制台指令等。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "list", "create", "delete", "toggle" }, ["description"] = "操作类型: 'list' (查看任务列表), 'create' (创建新任务), 'delete' (删除任务), 'toggle' (切换启用/禁用状态)" },
                        ["task_id"] = new JsonObject { ["type"] = "string", ["description"] = "计划任务唯一 ID (在 delete 或 toggle 操作时必需)" },
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "关联的服务器 ID (在 list 筛选或 create 创建时使用)" },
                        ["name"] = new JsonObject { ["type"] = "string", ["description"] = "计划任务名称 (在 create 时必需)" },
                        ["cron"] = new JsonObject { ["type"] = "string", ["description"] = "Cron 定时表达式 (例如 '0 4 * * *' 每天凌晨4点，'*/30 * * * *' 每30分钟，在 create 时必需)" },
                        ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "command", "backup", "restart", "start", "stop", "shell" }, ["description"] = "任务执行类型 (默认为 'command')" },
                        ["payload"] = new JsonObject { ["type"] = "string", ["description"] = "任务执行内容负载 (例如执行 command 时的具体控制台指令，如 'say 准备备份')" },
                        ["enable"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否启用任务 (在 create 时默认为 true，在 toggle 时指定目标状态)" },
                        ["run_when_offline"] = new JsonObject { ["type"] = "boolean", ["description"] = "服务器离线未运行时是否仍执行该计划任务 (默认为 true)" }
                    },
                    ["required"] = new JsonArray { "action" }
                }
            }
        });
    }

    private async Task<string?> HandleBackupFrpToolAsync(
        string name,
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        bool assumeConfirmed,
        string userId,
        bool canPromptForConfirmation)
    {
        switch (name)
        {
            case "create_server_backup":
                return await HandleCreateServerBackupAsync(args, onToolExecuted, userId);

            case "list_server_backups":
                return await HandleListServerBackupsAsync(args, onToolExecuted);

            case "restore_server_backup":
                return await HandleRestoreServerBackupAsync(args, onToolExecuted, assumeConfirmed, canPromptForConfirmation);

            case "delete_server_backup":
                return await HandleDeleteServerBackupAsync(args, onToolExecuted, assumeConfirmed, canPromptForConfirmation);

            case "query_frp_status":
                return await HandleQueryFrpStatusAsync(args, onToolExecuted);

            case "control_frp_tunnel":
                return await HandleControlFrpTunnelAsync(args, onToolExecuted);

            case "manage_scheduled_tasks":
                return await HandleManageScheduledTasksAsync(args, onToolExecuted);

            default:
                return null;
        }
    }

    private static string GetServerBackupDirectory(McServerInfo.ServerInfo server)
    {
        string backupDir = Path.Combine(server.Base, "mslx-backups");
        if (server.BackupPath == "MSLX://Backup/Data")
        {
            backupDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Backups", $"Backups_{server.Name}_{server.ID}");
        }
        else if (server.BackupPath != "MSLX://Backup/Instance" && !string.IsNullOrEmpty(server.BackupPath))
        {
            backupDir = server.BackupPath;
        }
        return backupDir;
    }

    private static List<object> GetBackupFilesFromDir(string backupDir)
    {
        if (!Directory.Exists(backupDir)) return new List<object>();

        var dirInfo = new DirectoryInfo(backupDir);
        var files = dirInfo.GetFiles("mslx-backup_*.zip");

        return files.Select(file =>
        {
            long len = file.Length;
            string formattedSize = len switch
            {
                >= 1073741824 => $"{len / 1024.0 / 1024.0 / 1024.0:F2} GB",
                >= 1048576 => $"{len / 1024.0 / 1024.0:F2} MB",
                >= 1024 => $"{len / 1024.0:F2} KB",
                _ => $"{len} Bytes"
            };

            return new
            {
                fileName = file.Name,
                fileSize = file.Length,
                fileSizeFormatted = formattedSize,
                createTime = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                timestamp = new DateTimeOffset(file.LastWriteTime).ToUnixTimeSeconds()
            };
        })
        .OrderByDescending(x => x.timestamp)
        .Cast<object>()
        .ToList();
    }

    private async Task<string> HandleCreateServerBackupAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        string userId)
    {
        int targetId = 0;
        if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
        {
            int.TryParse(args["server_id"]!.ToString(), out targetId);
        }

        var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
        var targetServer = targetId > 0
            ? serverList.FirstOrDefault(s => s.ID == targetId)
            : serverList.LastOrDefault();

        if (targetServer == null)
        {
            return "[ERROR] 未找到指定的服务器实例。请检查 server_id 是否正确。";
        }

        IInstanceBackupService? backupService = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            backupService = scope.ServiceProvider.GetService<IInstanceBackupService>()
                ?? _serviceProvider.GetService<IInstanceBackupService>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取 IInstanceBackupService 异常");
        }

        if (backupService == null)
        {
            return "[ERROR] 系统未能获取到实例备份服务 (IInstanceBackupService)，可能宿主未注册该服务。";
        }

        string opUserId = !string.IsNullOrWhiteSpace(userId)
            ? userId
            : (args != null && args.ContainsKey("user_id") ? args["user_id"]?.ToString() ?? "" : "");

        bool started = backupService.StartBackupServer((uint)targetServer.ID, opUserId);
        if (started)
        {
            try
            {
                await onToolExecuted("create_server_backup", new
                {
                    serverId = targetServer.ID,
                    serverName = targetServer.Name,
                    status = "started"
                });
            }
            catch { }

            return $"[SUCCESS] 已成功为服务器 '{targetServer.Name}' (ID: {targetServer.ID}) 触发全量备份任务。后台正在打包世界存档，完成后备份文件将存放在备份目录中。";
        }
        else
        {
            return $"[ERROR] 触发服务器 '{targetServer.Name}' (ID: {targetServer.ID}) 备份失败，可能当前已有备份任务在进行中或实例上下文未就绪。";
        }
    }

    private async Task<string> HandleListServerBackupsAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted)
    {
        int targetId = 0;
        if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
        {
            int.TryParse(args["server_id"]!.ToString(), out targetId);
        }

        var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
        if (serverList.Count == 0)
        {
            return "[INFO] 当前系统中没有任何服务器实例。";
        }

        if (targetId > 0)
        {
            var server = serverList.FirstOrDefault(s => s.ID == targetId);
            if (server == null)
            {
                return $"[ERROR] 未找到 ID 为 {targetId} 的服务器实例。";
            }

            string backupDir = GetServerBackupDirectory(server);
            var backups = GetBackupFilesFromDir(backupDir);

            var res = new
            {
                serverId = server.ID,
                serverName = server.Name,
                backupDir = backupDir,
                count = backups.Count,
                backups = backups
            };

            try
            {
                await onToolExecuted("list_server_backups", res);
            }
            catch { }

            return JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            var allResults = serverList.Select(server =>
            {
                string backupDir = GetServerBackupDirectory(server);
                var backups = GetBackupFilesFromDir(backupDir);
                return new
                {
                    serverId = server.ID,
                    serverName = server.Name,
                    backupDir = backupDir,
                    count = backups.Count,
                    backups = backups
                };
            }).ToList();

            try
            {
                await onToolExecuted("list_server_backups", new { servers = allResults });
            }
            catch { }

            return JsonSerializer.Serialize(allResults, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private async Task<string> HandleRestoreServerBackupAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        bool assumeConfirmed,
        bool canPromptForConfirmation)
    {
        int targetId = 0;
        if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
        {
            int.TryParse(args["server_id"]!.ToString(), out targetId);
        }
        string? fileName = args != null && args.ContainsKey("file_name") ? args["file_name"]?.ToString() : null;

        if (targetId <= 0 || string.IsNullOrWhiteSpace(fileName))
        {
            return "[ERROR] 缺少必要参数：需要提供有效的 server_id 与 file_name。";
        }

        var server = MSLX.SDK.MSLX.Config.Servers.GetServer((uint)targetId);
        if (server == null)
        {
            return $"[ERROR] 未找到 ID 为 {targetId} 的服务器实例。";
        }

        string safeFileName = Path.GetFileName(fileName);
        if (safeFileName != fileName || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return "[ERROR] 非法备份文件名：仅允许指定 .zip 格式且禁止包含路径跳转字符。";
        }

        string backupDir = GetServerBackupDirectory(server);
        string fullPath = Path.Combine(backupDir, safeFileName);
        if (!File.Exists(fullPath))
        {
            return $"[ERROR] 备份文件不存在：在目录 `{backupDir}` 中未找到 `{safeFileName}`。";
        }

        if (_lifecycleService.IsServerRunning((uint)server.ID))
        {
            return $"[ERROR] 还原失败：服务器 '{server.Name}' 当前正在运行中！为防止数据冲突与存档损坏，请先停止服务器后再执行还原。";
        }

        bool isConfirmed = assumeConfirmed || (args != null && args.ContainsKey("confirmed") && args["confirmed"]?.GetValue<bool>() == true);
        if (!isConfirmed)
        {
            if (!canPromptForConfirmation) return WebPanelConfirmationRequired;

            string confirmationId = CreateOrGetPendingOperation("restore_server_backup", args?.ToJsonString() ?? "{}", server.ID, safeFileName, "edit");
            var confirmData = new
            {
                serverId = server.ID,
                filePath = safeFileName,
                action = "edit",
                requiresConfirmation = true,
                confirmed = false,
                confirmationId = confirmationId
            };
            await onToolExecuted("restore_server_backup", confirmData);
            return $"[PENDING_CONFIRMATION] 高风险警告：准备将备份 `{safeFileName}` 还原至服务器 '{server.Name}' (ID: {server.ID})！还原操作将覆盖当前服务器根目录中的现有文件。操作已挂起，前端已弹出确认授权按钮，请用户点击确认后自动执行。";
        }

        try
        {
            ZipFile.ExtractToDirectory(fullPath, server.Base, overwriteFiles: true);

            try
            {
                await onToolExecuted("restore_server_backup", new
                {
                    serverId = server.ID,
                    filePath = safeFileName,
                    status = "restored"
                });
            }
            catch { }

            return $"[SUCCESS] 已成功将备份 `{safeFileName}` 还原覆盖至服务器 '{server.Name}' 目录 ({server.Base})。在启动服务器前，请检查重要配置是否正常。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "还原备份 {FileName} 到服务器 {ServerId} 失败", safeFileName, server.ID);
            return $"[ERROR] 还原备份失败: {ex.Message}";
        }
    }

    private async Task<string> HandleDeleteServerBackupAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        bool assumeConfirmed,
        bool canPromptForConfirmation)
    {
        int targetId = 0;
        if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
        {
            int.TryParse(args["server_id"]!.ToString(), out targetId);
        }
        string? fileName = args != null && args.ContainsKey("file_name") ? args["file_name"]?.ToString() : null;

        if (targetId <= 0 || string.IsNullOrWhiteSpace(fileName))
        {
            return "[ERROR] 缺少必要参数：需要提供有效的 server_id 与 file_name。";
        }

        var server = MSLX.SDK.MSLX.Config.Servers.GetServer((uint)targetId);
        if (server == null)
        {
            return $"[ERROR] 未找到 ID 为 {targetId} 的服务器实例。";
        }

        string safeFileName = Path.GetFileName(fileName);
        if (safeFileName != fileName || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return "[ERROR] 非法备份文件名：仅允许删除 .zip 格式备份文件且禁止包含路径跳转字符。";
        }

        string backupDir = GetServerBackupDirectory(server);
        string fullPath = Path.Combine(backupDir, safeFileName);
        if (!File.Exists(fullPath))
        {
            return $"[ERROR] 备份文件不存在：在目录 `{backupDir}` 中未找到 `{safeFileName}`。";
        }

        bool isConfirmed = assumeConfirmed || (args != null && args.ContainsKey("confirmed") && args["confirmed"]?.GetValue<bool>() == true);
        if (!isConfirmed)
        {
            if (!canPromptForConfirmation) return WebPanelConfirmationRequired;

            string confirmationId = CreateOrGetPendingOperation("delete_server_backup", args?.ToJsonString() ?? "{}", server.ID, safeFileName, "delete");
            var confirmData = new
            {
                serverId = server.ID,
                filePath = safeFileName,
                action = "delete",
                requiresConfirmation = true,
                confirmed = false,
                confirmationId = confirmationId
            };
            await onToolExecuted("delete_server_backup", confirmData);
            return $"[PENDING_CONFIRMATION] 高风险警告：准备永久删除服务器 '{server.Name}' 的备份文件 `{safeFileName}`！此操作不可逆。操作已挂起，前端已弹出确认按钮，请用户点击确认授权后自动执行。";
        }

        try
        {
            File.Delete(fullPath);

            try
            {
                await onToolExecuted("delete_server_backup", new
                {
                    serverId = server.ID,
                    filePath = safeFileName,
                    status = "deleted"
                });
            }
            catch { }

            return $"[SUCCESS] 服务器 '{server.Name}' 的备份文件 `{safeFileName}` 已成功永久删除。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除备份文件 {FullPath} 失败", fullPath);
            return $"[ERROR] 删除备份文件失败: {ex.Message}";
        }
    }

    private async Task<string> HandleQueryFrpStatusAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted)
    {
        IFrpProcessService? frpService = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            frpService = scope.ServiceProvider.GetService<IFrpProcessService>()
                ?? _serviceProvider.GetService<IFrpProcessService>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取 IFrpProcessService 异常");
        }

        if (frpService == null)
        {
            return "[ERROR] 系统未能获取到 FRP 进程服务 (IFrpProcessService)。";
        }

        int tunnelId = 0;
        if (args != null && args.ContainsKey("tunnel_id") && args["tunnel_id"] != null)
        {
            int.TryParse(args["tunnel_id"]!.ToString(), out tunnelId);
        }

        if (tunnelId > 0)
        {
            var frpConfig = MSLX.SDK.MSLX.Config.Frp.GetFrpConfig(tunnelId);
            if (frpConfig == null)
            {
                return $"[ERROR] 未找到 ID 为 {tunnelId} 的 FRP 隧道配置。";
            }

            bool isRunning = frpService.IsFrpRunning(tunnelId);
            var logs = frpService.GetLogs(tunnelId) ?? new List<string>();

            var detail = new
            {
                id = tunnelId,
                name = frpConfig["Name"]?.ToString() ?? "",
                service = frpConfig["Service"]?.ToString() ?? "",
                configType = frpConfig["ConfigType"]?.ToString() ?? "",
                status = isRunning ? "running" : "stopped",
                isRunning = isRunning,
                recentLogs = logs.TakeLast(30).ToList()
            };

            try
            {
                await onToolExecuted("query_frp_status", detail);
            }
            catch { }

            return JsonSerializer.Serialize(detail, new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            var frpList = MSLX.SDK.MSLX.Config.Frp.ReadFrpList();
            var list = frpList.Select(item =>
            {
                int id = item["ID"]?.Value<int>() ?? 0;
                bool isRunning = frpService.IsFrpRunning(id);
                return new
                {
                    id = id,
                    name = item["Name"]?.Value<string>() ?? "",
                    service = item["Service"]?.Value<string>() ?? "",
                    configType = item["ConfigType"]?.Value<string>() ?? "",
                    status = isRunning ? "running" : "stopped",
                    isRunning = isRunning
                };
            }).ToList();

            var res = new
            {
                total = list.Count,
                tunnels = list
            };

            try
            {
                await onToolExecuted("query_frp_status", res);
            }
            catch { }

            return JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private async Task<string> HandleControlFrpTunnelAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted)
    {
        IFrpProcessService? frpService = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            frpService = scope.ServiceProvider.GetService<IFrpProcessService>()
                ?? _serviceProvider.GetService<IFrpProcessService>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取 IFrpProcessService 异常");
        }

        if (frpService == null)
        {
            return "[ERROR] 系统未能获取到 FRP 进程服务 (IFrpProcessService)。";
        }

        int tunnelId = 0;
        if (args != null && args.ContainsKey("tunnel_id") && args["tunnel_id"] != null)
        {
            int.TryParse(args["tunnel_id"]!.ToString(), out tunnelId);
        }
        string action = args != null && args.ContainsKey("action") ? (args["action"]?.ToString()?.ToLower() ?? "") : "";

        if (tunnelId <= 0 || string.IsNullOrWhiteSpace(action))
        {
            return "[ERROR] 缺少必要参数：需要提供有效的 tunnel_id 与 action ('start', 'stop', 'log')。";
        }

        if (!MSLX.SDK.MSLX.Config.Frp.IsFrpIdValid(tunnelId))
        {
            return $"[ERROR] 未找到 ID 为 {tunnelId} 的 FRP 隧道配置。";
        }

        switch (action)
        {
            case "start":
            {
                var (success, msg) = frpService.StartFrp(tunnelId);
                try
                {
                    await onToolExecuted("control_frp_tunnel", new { tunnelId, action, success, message = msg });
                }
                catch { }

                return success
                    ? $"[SUCCESS] FRP 隧道 (ID: {tunnelId}) 启动成功：{msg}"
                    : $"[ERROR] FRP 隧道 (ID: {tunnelId}) 启动失败：{msg}";
            }

            case "stop":
            {
                bool success = frpService.StopFrp(tunnelId);
                try
                {
                    await onToolExecuted("control_frp_tunnel", new { tunnelId, action, success });
                }
                catch { }

                return success
                    ? $"[SUCCESS] FRP 隧道 (ID: {tunnelId}) 已成功停止。"
                    : $"[ERROR] FRP 隧道 (ID: {tunnelId}) 停止失败或当前未处于运行状态。";
            }

            case "log":
            case "logs":
            {
                var logs = frpService.GetLogs(tunnelId) ?? new List<string>();
                string logText = logs.Count > 0 ? string.Join("\n", logs.TakeLast(50)) : "(暂无日志输出)";
                try
                {
                    await onToolExecuted("control_frp_tunnel", new { tunnelId, action, logCount = logs.Count });
                }
                catch { }

                return $"FRP 隧道 (ID: {tunnelId}) 最近日志：\n```\n{logText}\n```";
            }

            default:
                return $"[ERROR] 不支持的 action 操作 '{action}'。仅支持 'start' (启动), 'stop' (停止), 'log' (日志)。";
        }
    }

    private async Task<string> HandleManageScheduledTasksAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted)
    {
        string action = args != null && args.ContainsKey("action") ? (args["action"]?.ToString()?.ToLower() ?? "") : "";
        if (string.IsNullOrWhiteSpace(action))
        {
            return "[ERROR] 缺少必要参数 action。支持的操作为: 'list', 'create', 'delete', 'toggle'。";
        }

        switch (action)
        {
            case "list":
            {
                int serverId = 0;
                if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                {
                    int.TryParse(args["server_id"]!.ToString(), out serverId);
                }

                var tasks = serverId > 0
                    ? MSLX.SDK.MSLX.Config.Tasks.GetTasksByInstanceId((uint)serverId)
                    : MSLX.SDK.MSLX.Config.Tasks.GetTaskList();

                var res = new
                {
                    total = tasks.Count,
                    serverIdFilter = serverId > 0 ? serverId : (int?)null,
                    tasks = tasks.Select(t => new
                    {
                        id = t.ID,
                        instanceId = t.InstanceId,
                        name = t.Name,
                        type = t.Type,
                        cron = t.Cron,
                        payload = t.Payload,
                        enable = t.Enable,
                        runWhenOffline = t.RunWhenOffline,
                        lastRunTime = t.LastRunTime?.ToString("yyyy-MM-dd HH:mm:ss")
                    }).ToList()
                };

                try
                {
                    await onToolExecuted("manage_scheduled_tasks", res);
                }
                catch { }

                return JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true });
            }

            case "create":
            {
                int serverId = 0;
                if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
                {
                    int.TryParse(args["server_id"]!.ToString(), out serverId);
                }
                if (serverId <= 0)
                {
                    var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
                    if (serverList.Count > 0)
                    {
                        serverId = serverList.Last().ID;
                    }
                    else
                    {
                        return "[ERROR] 无法创建计划任务：当前系统中没有可关联的服务器实例。";
                    }
                }

                string name = args != null && args.ContainsKey("name") ? args["name"]?.ToString() ?? "" : "";
                string cron = args != null && args.ContainsKey("cron") ? args["cron"]?.ToString() ?? "" : "";
                string type = args != null && args.ContainsKey("type") ? args["type"]?.ToString()?.ToLower() ?? "command" : "command";
                string payload = args != null && args.ContainsKey("payload") ? args["payload"]?.ToString() ?? "" : "";
                bool enable = args != null && args.ContainsKey("enable") && args["enable"] != null ? args["enable"]!.GetValue<bool>() : true;
                bool runWhenOffline = args != null && args.ContainsKey("run_when_offline") && args["run_when_offline"] != null ? args["run_when_offline"]!.GetValue<bool>() : true;

                if (string.IsNullOrWhiteSpace(name))
                {
                    return "[ERROR] 创建计划任务失败：缺少任务名称 name。";
                }
                if (string.IsNullOrWhiteSpace(cron))
                {
                    return "[ERROR] 创建计划任务失败：缺少 Cron 表达式 cron。";
                }

                try
                {
                    var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var format = parts.Length > 5 ? Cronos.CronFormat.IncludeSeconds : Cronos.CronFormat.Standard;
                    Cronos.CronExpression.Parse(cron, format);
                }
                catch (Exception ex)
                {
                    return $"[ERROR] Cron 表达式格式无效: {ex.Message} (标准示例: '0 4 * * *' 表示每天凌晨4点执行)";
                }

                var newTask = new ScheduleTask
                {
                    ID = Guid.NewGuid().ToString("N"),
                    InstanceId = (uint)serverId,
                    Name = name,
                    Cron = cron,
                    Type = type,
                    Payload = payload,
                    Enable = enable,
                    RunWhenOffline = runWhenOffline,
                    LastRunTime = null
                };

                bool created = MSLX.SDK.MSLX.Config.Tasks.CreateTask(newTask);
                if (!created)
                {
                    return "[ERROR] 保存计划任务失败，可能由于任务 ID 冲突或配置写入错误。";
                }

                try
                {
                    await onToolExecuted("manage_scheduled_tasks", new { action = "create", task = newTask });
                }
                catch { }

                return $"[SUCCESS] 成功创建计划任务 '{newTask.Name}' (ID: {newTask.ID})！类型: {newTask.Type}，Cron: '{newTask.Cron}'，关联服务器 ID: {newTask.InstanceId}，启用状态: {(newTask.Enable ? "已启用" : "已禁用")}。";
            }

            case "delete":
            {
                string taskId = args != null && args.ContainsKey("task_id") ? args["task_id"]?.ToString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    return "[ERROR] 删除计划任务失败：缺少 task_id。";
                }

                var task = MSLX.SDK.MSLX.Config.Tasks.GetTask(taskId);
                if (task == null)
                {
                    return $"[ERROR] 未找到 ID 为 {taskId} 的计划任务。";
                }

                bool deleted = MSLX.SDK.MSLX.Config.Tasks.DeleteTask(taskId);
                try
                {
                    await onToolExecuted("manage_scheduled_tasks", new { action = "delete", taskId, success = deleted });
                }
                catch { }

                return deleted
                    ? $"[SUCCESS] 计划任务 '{task.Name}' (ID: {taskId}) 已成功删除。"
                    : $"[ERROR] 删除计划任务 {taskId} 失败。";
            }

            case "toggle":
            {
                string taskId = args != null && args.ContainsKey("task_id") ? args["task_id"]?.ToString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    return "[ERROR] 切换任务状态失败：缺少 task_id。";
                }

                var task = MSLX.SDK.MSLX.Config.Tasks.GetTask(taskId);
                if (task == null)
                {
                    return $"[ERROR] 未找到 ID 为 {taskId} 的计划任务。";
                }

                if (args != null && args.ContainsKey("enable") && args["enable"] != null)
                {
                    task.Enable = args["enable"]!.GetValue<bool>();
                }
                else
                {
                    task.Enable = !task.Enable;
                }

                bool updated = MSLX.SDK.MSLX.Config.Tasks.UpdateTask(task);
                try
                {
                    await onToolExecuted("manage_scheduled_tasks", new { action = "toggle", taskId, enable = task.Enable, success = updated });
                }
                catch { }

                return updated
                    ? $"[SUCCESS] 计划任务 '{task.Name}' (ID: {taskId}) 状态已切换为：{(task.Enable ? "已启用" : "已禁用")}。"
                    : $"[ERROR] 更新计划任务 {taskId} 状态失败。";
            }

            default:
                return $"[ERROR] 不支持的操作类型 '{action}'。仅支持 'list', 'create', 'delete', 'toggle'。";
        }
    }
}
