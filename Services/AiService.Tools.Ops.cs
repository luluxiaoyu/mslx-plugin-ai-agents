using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MSLX.SDK.Models;

namespace MSLX.Plugin.AiAgents.Services;

public partial class AiService
{
    private void RegisterOpsTools(JsonArray tools)
    {
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "force_kill_server",
                ["description"] = "强制终止指定 Minecraft 服务器的运行进程 (Kill / SIGKILL)。仅在服务器卡死、无响应或常规停止指令失败时调用。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" }
                    }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "delete_server_instance",
                ["description"] = "彻底删除 Minecraft 服务器实例配置及其文件数据。注意：当 delete_files 为 true 时，将永久清除该服务器目录下的所有文件和存档，此操作属于高危操作，将触发系统二次确认卡片！",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "要删除的服务器 ID" },
                        ["delete_files"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否同时彻底删除服务器所有本地文件与世界存档。true 为物理全量删除（需二次确认），false 仅从列表注销配置。" }
                    },
                    ["required"] = new JsonArray { "server_id" }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "agree_server_eula",
                ["description"] = "同意 Minecraft 最终用户许可协议 (EULA)。修改服务器目录下的 eula.txt 为 eula=true，使服务端能够正常启动运行。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                        ["agree"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否同意 EULA 协议，默认为 true" }
                    }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "query_players",
                ["description"] = "查询指定 Minecraft 服务器的在线玩家与历史活动玩家列表（自动结合实时在线状态、usercache.json 与存档数据）。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" }
                    }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "manage_whitelist",
                ["description"] = "管理 Minecraft 服务器白名单 (whitelist.json)。支持 list（查看）、add（添加玩家）、remove（移除玩家）。修改后若服务器正在运行将自动执行 whitelist reload 使其即时生效。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                        ["action"] = new JsonObject { ["type"] = "string", ["description"] = "操作类型: 'list' (列出白名单), 'add' (添加玩家), 'remove' (移除玩家)", ["enum"] = new JsonArray { "list", "add", "remove" } },
                        ["player_name"] = new JsonObject { ["type"] = "string", ["description"] = "玩家名称（add 或 remove 时必填）" },
                        ["uuid"] = new JsonObject { ["type"] = "string", ["description"] = "玩家 UUID（可选，未提供时将自动查询 usercache.json 或生成离线 UUID）" }
                    },
                    ["required"] = new JsonArray { "action" }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "manage_ops",
                ["description"] = "管理 Minecraft 服务器管理员 (OP) 权限列表 (ops.json)。支持 list（查看）、add（添加管理员）、remove（取消管理员）。修改后若服务器正在运行将自动发送控制台指令即时生效。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                        ["action"] = new JsonObject { ["type"] = "string", ["description"] = "操作类型: 'list' (列出管理员), 'add' (添加管理员), 'remove' (取消管理员)", ["enum"] = new JsonArray { "list", "add", "remove" } },
                        ["player_name"] = new JsonObject { ["type"] = "string", ["description"] = "玩家名称（add 或 remove 时必填）" },
                        ["level"] = new JsonObject { ["type"] = "number", ["description"] = "OP 权限等级 (1-4)，默认 4 (完全管理权限)" },
                        ["bypasses_player_limit"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否无视服务器满人限制进入服务器，默认 false" },
                        ["uuid"] = new JsonObject { ["type"] = "string", ["description"] = "玩家 UUID（可选，未提供时将自动查询 usercache.json 或生成离线 UUID）" }
                    },
                    ["required"] = new JsonArray { "action" }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "manage_bans",
                ["description"] = "管理 Minecraft 服务器封禁黑名单 (banned-players.json 与 banned-ips.json)。支持 list（列出封禁）、ban_player（封禁玩家）、unban_player（解封玩家）、ban_ip（封禁 IP）、unban_ip（解封 IP）。修改后若服务器正在运行将自动执行控制台封禁/解封指令。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合【当前用户界面上下文】的主机 ID 或最新服务器" },
                        ["action"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "操作类型: 'list' (列出所有封禁), 'ban_player' (封禁玩家), 'unban_player' (解封玩家), 'ban_ip' (封禁IP), 'unban_ip' (解封IP)",
                            ["enum"] = new JsonArray { "list", "ban_player", "unban_player", "ban_ip", "unban_ip" }
                        },
                        ["player_name"] = new JsonObject { ["type"] = "string", ["description"] = "玩家名称（ban_player 或 unban_player 时必填）" },
                        ["ip"] = new JsonObject { ["type"] = "string", ["description"] = "IP 地址（ban_ip 或 unban_ip 时必填）" },
                        ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "封禁原因描述（可选，默认 'Banned by operator'）" },
                        ["source"] = new JsonObject { ["type"] = "string", ["description"] = "封禁来源执行者（可选，默认 'AI-Console'）" },
                        ["expires"] = new JsonObject { ["type"] = "string", ["description"] = "过期时间（可选，默认 'forever' 永久）" }
                    },
                    ["required"] = new JsonArray { "action" }
                }
            }
        });
    }

    private async Task<string?> HandleOpsToolAsync(string name, JsonObject? args, Func<string, object, Task> onToolExecuted,
        bool assumeConfirmed, string userId)
    {
        switch (name)
        {
            case "force_kill_server":
            {
                var (targetServer, error) = ResolveTargetServer(args);
                if (targetServer == null) return error ?? "[ERROR] 未找到服务器实例。";

                bool wasRunning = _lifecycleService.IsServerRunning((uint)targetServer.ID);
                bool success = _lifecycleService.ForceKillServer((uint)targetServer.ID);

                await onToolExecuted("force_kill_server", new
                {
                    serverId = targetServer.ID,
                    serverName = targetServer.Name,
                    wasRunning,
                    success
                });

                return success
                    ? $"[SUCCESS] 已向服务器 #{targetServer.ID} ({targetServer.Name}) 发送强杀信号，进程已强制终止。"
                    : $"[INFO] 强制终止服务器 #{targetServer.ID} ({targetServer.Name}) 信号已发送，但该服务器可能未在运行。";
            }

            case "delete_server_instance":
            {
                var (targetServer, error) = ResolveTargetServer(args);
                if (targetServer == null) return error ?? "[ERROR] 未找到服务器实例。";

                bool deleteFiles = false;
                if (args != null && args.ContainsKey("delete_files") && args["delete_files"] != null)
                {
                    bool.TryParse(args["delete_files"]!.ToString(), out deleteFiles);
                }

                if (!assumeConfirmed)
                {


                    string confirmationId = CreatePendingOperation("delete_server_instance", args?.ToJsonString() ?? "{}", targetServer.ID, targetServer.Name, deleteFiles ? "delete_data" : "remove_instance", userId);
                    var confirmData = new
                    {
                        serverId = targetServer.ID,
                        serverName = targetServer.Name,
                        deleteFiles,
                        action = "delete",
                        requiresConfirmation = true,
                        confirmed = false,
                        confirmationId = confirmationId
                    };
                    await onToolExecuted("delete_server_instance", confirmData);
                    return PendingMessage(confirmationId);
                }

                if (_lifecycleService.IsServerRunning((uint)targetServer.ID))
                {
                    try
                    {
                        _lifecycleService.ForceKillServer((uint)targetServer.ID);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "删除服务器前强制杀进程异常");
                    }
                }

                bool success = MSLX.SDK.MSLX.Config.Servers.DeleteServer((uint)targetServer.ID, deleteFiles);
                await onToolExecuted("delete_server_instance", new
                {
                    serverId = targetServer.ID,
                    serverName = targetServer.Name,
                    deleteFiles,
                    success
                });

                return success
                    ? $"[SUCCESS] 服务器 #{targetServer.ID} ({targetServer.Name}) 实例已成功删除{(deleteFiles ? "（物理文件与数据已全量清除）" : "（保留服务端本地文件）")}。"
                    : $"[ERROR] 删除服务器 #{targetServer.ID} ({targetServer.Name}) 失败。";
            }

            case "agree_server_eula":
            {
                var (targetServer, error) = ResolveTargetServer(args);
                if (targetServer == null) return error ?? "[ERROR] 未找到服务器实例。";

                bool agree = true;
                if (args != null && args.ContainsKey("agree") && args["agree"] != null)
                {
                    bool.TryParse(args["agree"]!.ToString(), out agree);
                }

                bool success = false;
                try
                {
                    success = await _lifecycleService.AgreeEULA((uint)targetServer.ID, agree);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "调用 AgreeEULA 异常，尝试直接更新 eula.txt");
                }

                string serverDir = GetServerDirectory(targetServer);
                string eulaFile = Path.Combine(serverDir, "eula.txt");
                try
                {
                    if (Directory.Exists(serverDir))
                    {
                        string content = $"#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://aka.ms/MinecraftEULA).\n#{DateTime.UtcNow:ddd MMM dd HH:mm:ss UTC yyyy}\neula={agree.ToString().ToLowerInvariant()}\n";
                        File.WriteAllText(eulaFile, content, Encoding.UTF8);
                        success = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "写入 eula.txt 失败");
                }

                await onToolExecuted("agree_server_eula", new
                {
                    serverId = targetServer.ID,
                    serverName = targetServer.Name,
                    agree,
                    success
                });

                return success
                    ? $"[SUCCESS] 已成功为服务器 #{targetServer.ID} ({targetServer.Name}) 设置 EULA 协议为 {(agree ? "同意 (eula=true)" : "拒绝 (eula=false)")}。"
                    : $"[ERROR] 设置服务器 #{targetServer.ID} EULA 协议失败。";
            }

            case "query_players":
            {
                var (targetServer, error) = ResolveTargetServer(args);
                if (targetServer == null) return error ?? "[ERROR] 未找到服务器实例。";

                List<string> onlinePlayers;
                try
                {
                    onlinePlayers = _lifecycleService.GetOnlinePlayers((uint)targetServer.ID) ?? new List<string>();
                }
                catch
                {
                    onlinePlayers = new List<string>();
                }

                string serverDir = GetServerDirectory(targetServer);
                var cachedPlayers = new List<CachedPlayerRecord>();
                string usercachePath = Path.Combine(serverDir, "usercache.json");

                if (File.Exists(usercachePath))
                {
                    try
                    {
                        string json = File.ReadAllText(usercachePath, Encoding.UTF8);
                        var arr = JsonNode.Parse(json)?.AsArray();
                        if (arr != null)
                        {
                            foreach (var item in arr)
                            {
                                if (item is JsonObject obj)
                                {
                                    string cName = obj["name"]?.GetValue<string>() ?? "";
                                    string uuid = obj["uuid"]?.GetValue<string>() ?? "";
                                    string expiresOn = obj["expiresOn"]?.GetValue<string>() ?? "";
                                    if (!string.IsNullOrEmpty(cName))
                                    {
                                        cachedPlayers.Add(new CachedPlayerRecord
                                        {
                                            Name = cName,
                                            Uuid = uuid,
                                            ExpiresOn = expiresOn
                                        });
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "解析 usercache.json 异常");
                    }
                }

                string[] worldDirs = ["world", "world_nether", "world_the_end"];
                var playerLastSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                foreach (var wDir in worldDirs)
                {
                    string pdDir = Path.Combine(serverDir, wDir, "playerdata");
                    if (Directory.Exists(pdDir))
                    {
                        try
                        {
                            foreach (var datFile in Directory.GetFiles(pdDir, "*.dat"))
                            {
                                string uuid = Path.GetFileNameWithoutExtension(datFile);
                                var lastWrite = File.GetLastWriteTime(datFile);
                                if (!playerLastSeen.TryGetValue(uuid, out var existing) || lastWrite > existing)
                                {
                                    playerLastSeen[uuid] = lastWrite;
                                }
                            }
                        }
                        catch { }
                    }
                }

                foreach (var cp in cachedPlayers)
                {
                    if (playerLastSeen.TryGetValue(cp.Uuid, out var lastTime))
                    {
                        cp.LastSeen = lastTime;
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine($"[PLAYERS_INFO] 服务器 #{targetServer.ID} ({targetServer.Name}) 玩家数据：");
                sb.AppendLine($"- 实时在线玩家 ({onlinePlayers.Count} 人): {(onlinePlayers.Count > 0 ? string.Join(", ", onlinePlayers) : "暂无玩家在线")}");
                sb.AppendLine($"- 历史玩家缓存 ({cachedPlayers.Count} 人):");
                if (cachedPlayers.Count == 0)
                {
                    sb.AppendLine("  暂无历史玩家记录。");
                }
                else
                {
                    foreach (var cp in cachedPlayers.Take(25))
                    {
                        string timeStr = cp.LastSeen.HasValue ? cp.LastSeen.Value.ToString("yyyy-MM-dd HH:mm:ss") : "无存档时间";
                        sb.AppendLine($"  • {cp.Name} (UUID: {cp.Uuid}, 最近活动: {timeStr})");
                    }
                    if (cachedPlayers.Count > 25)
                    {
                        sb.AppendLine($"  ... 以及其他 {cachedPlayers.Count - 25} 名历史玩家。");
                    }
                }

                await onToolExecuted("query_players", new
                {
                    serverId = targetServer.ID,
                    serverName = targetServer.Name,
                    onlinePlayers,
                    cachedCount = cachedPlayers.Count,
                    cachedPlayers
                });

                return sb.ToString().TrimEnd();
            }

            case "manage_whitelist":
            {
                var (targetServer, error) = ResolveTargetServer(args);
                if (targetServer == null) return error ?? "[ERROR] 未找到服务器实例。";

                string action = args?["action"]?.ToString()?.ToLowerInvariant() ?? "list";
                string playerName = args?["player_name"]?.ToString()?.Trim() ?? "";
                string inputUuid = args?["uuid"]?.ToString()?.Trim() ?? "";

                string serverDir = GetServerDirectory(targetServer);
                string whitelistPath = Path.Combine(serverDir, "whitelist.json");

                JsonArray whitelistArr;
                try
                {
                    if (File.Exists(whitelistPath))
                    {
                        string json = File.ReadAllText(whitelistPath, Encoding.UTF8);
                        whitelistArr = JsonNode.Parse(json)?.AsArray() ?? new JsonArray();
                    }
                    else
                    {
                        whitelistArr = new JsonArray();
                    }
                }
                catch
                {
                    whitelistArr = new JsonArray();
                }

                switch (action)
                {
                    case "list":
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"[WHITELIST] 服务器 #{targetServer.ID} ({targetServer.Name}) 白名单列表 (共 {whitelistArr.Count} 人):");
                        if (whitelistArr.Count == 0)
                        {
                            sb.AppendLine("白名单列表为空。");
                        }
                        else
                        {
                            foreach (var item in whitelistArr)
                            {
                                if (item is JsonObject obj)
                                {
                                    string wlName = obj["name"]?.GetValue<string>() ?? "Unknown";
                                    string uuid = obj["uuid"]?.GetValue<string>() ?? "";
                                    sb.AppendLine($"- {wlName} (UUID: {uuid})");
                                }
                            }
                        }

                        await onToolExecuted("manage_whitelist", new
                        {
                            serverId = targetServer.ID,
                            action = "list",
                            count = whitelistArr.Count,
                            whitelist = whitelistArr
                        });

                        return sb.ToString().TrimEnd();
                    }

                    case "add":
                    {
                        if (string.IsNullOrWhiteSpace(playerName))
                        {
                            return "[ERROR] 缺少 player_name 参数。添加白名单必须提供玩家名称。";
                        }

                        bool alreadyExists = false;
                        foreach (var item in whitelistArr)
                        {
                            if (item is JsonObject obj)
                            {
                                string existingName = obj["name"]?.GetValue<string>() ?? "";
                                if (string.Equals(existingName, playerName, StringComparison.OrdinalIgnoreCase))
                                {
                                    alreadyExists = true;
                                    break;
                                }
                            }
                        }

                        if (alreadyExists)
                        {
                            return $"[INFO] 玩家 {playerName} 已经存在于服务器 #{targetServer.ID} 的白名单中。";
                        }

                        string resolvedUuid = !string.IsNullOrWhiteSpace(inputUuid)
                            ? inputUuid
                            : ResolvePlayerUuid(serverDir, playerName);

                        whitelistArr.Add(new JsonObject
                        {
                            ["uuid"] = resolvedUuid,
                            ["name"] = playerName
                        });

                        Directory.CreateDirectory(serverDir);
                        File.WriteAllText(whitelistPath, whitelistArr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

                        if (_lifecycleService.IsServerRunning((uint)targetServer.ID))
                        {
                            _consoleService.SendCommand((uint)targetServer.ID, "whitelist reload", true);
                        }

                        await onToolExecuted("manage_whitelist", new
                        {
                            serverId = targetServer.ID,
                            action = "add",
                            playerName,
                            uuid = resolvedUuid,
                            success = true
                        });

                        return $"[SUCCESS] 已成功将玩家 {playerName} (UUID: {resolvedUuid}) 加入服务器 #{targetServer.ID} 白名单。";
                    }

                    case "remove":
                    {
                        if (string.IsNullOrWhiteSpace(playerName))
                        {
                            return "[ERROR] 缺少 player_name 参数。移除白名单必须提供玩家名称。";
                        }

                        int removedCount = 0;
                        for (int i = whitelistArr.Count - 1; i >= 0; i--)
                        {
                            if (whitelistArr[i] is JsonObject obj)
                            {
                                string existingName = obj["name"]?.GetValue<string>() ?? "";
                                string existingUuid = obj["uuid"]?.GetValue<string>() ?? "";
                                if (string.Equals(existingName, playerName, StringComparison.OrdinalIgnoreCase) ||
                                    (!string.IsNullOrEmpty(inputUuid) && string.Equals(existingUuid, inputUuid, StringComparison.OrdinalIgnoreCase)))
                                {
                                    whitelistArr.RemoveAt(i);
                                    removedCount++;
                                }
                            }
                        }

                        if (removedCount > 0)
                        {
                            File.WriteAllText(whitelistPath, whitelistArr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                            if (_lifecycleService.IsServerRunning((uint)targetServer.ID))
                            {
                                _consoleService.SendCommand((uint)targetServer.ID, "whitelist reload", true);
                            }
                        }

                        await onToolExecuted("manage_whitelist", new
                        {
                            serverId = targetServer.ID,
                            action = "remove",
                            playerName,
                            removedCount,
                            success = removedCount > 0
                        });

                        return removedCount > 0
                            ? $"[SUCCESS] 已将玩家 {playerName} 从服务器 #{targetServer.ID} 白名单中移除。"
                            : $"[INFO] 白名单中未找到玩家 {playerName}。";
                    }

                    default:
                        return $"[ERROR] 未知的 whitelist 操作类型: {action}";
                }
            }

            case "manage_ops":
            {
                var (targetServer, error) = ResolveTargetServer(args);
                if (targetServer == null) return error ?? "[ERROR] 未找到服务器实例。";

                string action = args?["action"]?.ToString()?.ToLowerInvariant() ?? "list";
                string playerName = args?["player_name"]?.ToString()?.Trim() ?? "";
                string inputUuid = args?["uuid"]?.ToString()?.Trim() ?? "";
                int level = 4;
                if (args != null && args.ContainsKey("level") && args["level"] != null)
                {
                    if (int.TryParse(args["level"]!.ToString(), out int parsedLevel))
                    {
                        level = Math.Clamp(parsedLevel, 1, 4);
                    }
                }
                bool bypass = args != null && args.ContainsKey("bypasses_player_limit") && args["bypasses_player_limit"]?.GetValue<bool>() == true;

                string serverDir = GetServerDirectory(targetServer);
                string opsPath = Path.Combine(serverDir, "ops.json");

                JsonArray opsArr;
                try
                {
                    if (File.Exists(opsPath))
                    {
                        string json = File.ReadAllText(opsPath, Encoding.UTF8);
                        opsArr = JsonNode.Parse(json)?.AsArray() ?? new JsonArray();
                    }
                    else
                    {
                        opsArr = new JsonArray();
                    }
                }
                catch
                {
                    opsArr = new JsonArray();
                }

                switch (action)
                {
                    case "list":
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"[OPS] 服务器 #{targetServer.ID} ({targetServer.Name}) 管理员列表 (共 {opsArr.Count} 人):");
                        if (opsArr.Count == 0)
                        {
                            sb.AppendLine("管理员列表为空。");
                        }
                        else
                        {
                            foreach (var item in opsArr)
                            {
                                if (item is JsonObject obj)
                                {
                                    string opName = obj["name"]?.GetValue<string>() ?? "Unknown";
                                    string uuid = obj["uuid"]?.GetValue<string>() ?? "";
                                    int opLevel = obj["level"] is JsonValue lv && lv.TryGetValue<int>(out int l) ? l : 4;
                                    bool canBypass = obj["bypassesPlayerLimit"] is JsonValue bp && bp.TryGetValue<bool>(out bool b) && b;
                                    sb.AppendLine($"- {opName} (等级: {opLevel}, UUID: {uuid}, 无视满人限制: {(canBypass ? "是" : "否")})");
                                }
                            }
                        }

                        await onToolExecuted("manage_ops", new
                        {
                            serverId = targetServer.ID,
                            action = "list",
                            count = opsArr.Count,
                            ops = opsArr
                        });

                        return sb.ToString().TrimEnd();
                    }

                    case "add":
                    {
                        if (string.IsNullOrWhiteSpace(playerName))
                        {
                            return "[ERROR] 缺少 player_name 参数。添加管理员必须提供玩家名称。";
                        }

                        JsonObject? existingObj = null;
                        foreach (var item in opsArr)
                        {
                            if (item is JsonObject obj)
                            {
                                string existingName = obj["name"]?.GetValue<string>() ?? "";
                                if (string.Equals(existingName, playerName, StringComparison.OrdinalIgnoreCase))
                                {
                                    existingObj = obj;
                                    break;
                                }
                            }
                        }

                        string resolvedUuid = !string.IsNullOrWhiteSpace(inputUuid)
                            ? inputUuid
                            : ResolvePlayerUuid(serverDir, playerName);

                        if (existingObj != null)
                        {
                            existingObj["level"] = level;
                            existingObj["bypassesPlayerLimit"] = bypass;
                            if (!string.IsNullOrEmpty(resolvedUuid))
                            {
                                existingObj["uuid"] = resolvedUuid;
                            }
                        }
                        else
                        {
                            opsArr.Add(new JsonObject
                            {
                                ["uuid"] = resolvedUuid,
                                ["name"] = playerName,
                                ["level"] = level,
                                ["bypassesPlayerLimit"] = bypass
                            });
                        }

                        Directory.CreateDirectory(serverDir);
                        File.WriteAllText(opsPath, opsArr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

                        if (_lifecycleService.IsServerRunning((uint)targetServer.ID))
                        {
                            _consoleService.SendCommand((uint)targetServer.ID, $"op {playerName}", true);
                        }

                        await onToolExecuted("manage_ops", new
                        {
                            serverId = targetServer.ID,
                            action = "add",
                            playerName,
                            level,
                            bypass,
                            uuid = resolvedUuid,
                            success = true
                        });

                        return $"[SUCCESS] 已将玩家 {playerName} (UUID: {resolvedUuid}) 设置为服务器 #{targetServer.ID} 管理员 (OP 等级: {level}, 无视满人: {(bypass ? "是" : "否")})。";
                    }

                    case "remove":
                    {
                        if (string.IsNullOrWhiteSpace(playerName))
                        {
                            return "[ERROR] 缺少 player_name 参数。取消管理员必须提供玩家名称。";
                        }

                        int removedCount = 0;
                        for (int i = opsArr.Count - 1; i >= 0; i--)
                        {
                            if (opsArr[i] is JsonObject obj)
                            {
                                string existingName = obj["name"]?.GetValue<string>() ?? "";
                                string existingUuid = obj["uuid"]?.GetValue<string>() ?? "";
                                if (string.Equals(existingName, playerName, StringComparison.OrdinalIgnoreCase) ||
                                    (!string.IsNullOrEmpty(inputUuid) && string.Equals(existingUuid, inputUuid, StringComparison.OrdinalIgnoreCase)))
                                {
                                    opsArr.RemoveAt(i);
                                    removedCount++;
                                }
                            }
                        }

                        if (removedCount > 0)
                        {
                            File.WriteAllText(opsPath, opsArr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                            if (_lifecycleService.IsServerRunning((uint)targetServer.ID))
                            {
                                _consoleService.SendCommand((uint)targetServer.ID, $"deop {playerName}", true);
                            }
                        }

                        await onToolExecuted("manage_ops", new
                        {
                            serverId = targetServer.ID,
                            action = "remove",
                            playerName,
                            removedCount,
                            success = removedCount > 0
                        });

                        return removedCount > 0
                            ? $"[SUCCESS] 已取消玩家 {playerName} 在服务器 #{targetServer.ID} 的管理员 (OP) 权限。"
                            : $"[INFO] 管理员列表中未找到玩家 {playerName}。";
                    }

                    default:
                        return $"[ERROR] 未知的 ops 操作类型: {action}";
                }
            }

            case "manage_bans":
            {
                var (targetServer, error) = ResolveTargetServer(args);
                if (targetServer == null) return error ?? "[ERROR] 未找到服务器实例。";

                string action = args?["action"]?.ToString()?.ToLowerInvariant() ?? "list";
                string playerName = args?["player_name"]?.ToString()?.Trim() ?? "";
                string ip = args?["ip"]?.ToString()?.Trim() ?? "";
                string reason = args?["reason"]?.ToString()?.Trim() ?? "Banned by administrator";
                string source = args?["source"]?.ToString()?.Trim() ?? "AI-Console";
                string expires = args?["expires"]?.ToString()?.Trim() ?? "forever";

                string serverDir = GetServerDirectory(targetServer);
                string bannedPlayersPath = Path.Combine(serverDir, "banned-players.json");
                string bannedIpsPath = Path.Combine(serverDir, "banned-ips.json");

                JsonArray bannedPlayersArr;
                try
                {
                    if (File.Exists(bannedPlayersPath))
                    {
                        string json = File.ReadAllText(bannedPlayersPath, Encoding.UTF8);
                        bannedPlayersArr = JsonNode.Parse(json)?.AsArray() ?? new JsonArray();
                    }
                    else
                    {
                        bannedPlayersArr = new JsonArray();
                    }
                }
                catch
                {
                    bannedPlayersArr = new JsonArray();
                }

                JsonArray bannedIpsArr;
                try
                {
                    if (File.Exists(bannedIpsPath))
                    {
                        string json = File.ReadAllText(bannedIpsPath, Encoding.UTF8);
                        bannedIpsArr = JsonNode.Parse(json)?.AsArray() ?? new JsonArray();
                    }
                    else
                    {
                        bannedIpsArr = new JsonArray();
                    }
                }
                catch
                {
                    bannedIpsArr = new JsonArray();
                }

                switch (action)
                {
                    case "list":
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"[BAN_LIST] 服务器 #{targetServer.ID} ({targetServer.Name}) 封禁名单：");
                        sb.AppendLine($"- 封禁玩家 (共 {bannedPlayersArr.Count} 人):");
                        if (bannedPlayersArr.Count == 0)
                        {
                            sb.AppendLine("  暂无封禁玩家记录。");
                        }
                        else
                        {
                            foreach (var item in bannedPlayersArr)
                            {
                                if (item is JsonObject obj)
                                {
                                    string bannedName = obj["name"]?.GetValue<string>() ?? "Unknown";
                                    string uuid = obj["uuid"]?.GetValue<string>() ?? "";
                                    string banReason = obj["reason"]?.GetValue<string>() ?? "No reason";
                                    string banExpires = obj["expires"]?.GetValue<string>() ?? "forever";
                                    sb.AppendLine($"  • {bannedName} (UUID: {uuid}, 原因: {banReason}, 过期: {banExpires})");
                                }
                            }
                        }

                        sb.AppendLine($"- 封禁 IP (共 {bannedIpsArr.Count} 条):");
                        if (bannedIpsArr.Count == 0)
                        {
                            sb.AppendLine("  暂无封禁 IP 记录。");
                        }
                        else
                        {
                            foreach (var item in bannedIpsArr)
                            {
                                if (item is JsonObject obj)
                                {
                                    string banIp = obj["ip"]?.GetValue<string>() ?? "";
                                    string banReason = obj["reason"]?.GetValue<string>() ?? "No reason";
                                    string banExpires = obj["expires"]?.GetValue<string>() ?? "forever";
                                    sb.AppendLine($"  • {banIp} (原因: {banReason}, 过期: {banExpires})");
                                }
                            }
                        }

                        await onToolExecuted("manage_bans", new
                        {
                            serverId = targetServer.ID,
                            action = "list",
                            bannedPlayersCount = bannedPlayersArr.Count,
                            bannedIpsCount = bannedIpsArr.Count,
                            bannedPlayers = bannedPlayersArr,
                            bannedIps = bannedIpsArr
                        });

                        return sb.ToString().TrimEnd();
                    }

                    case "ban_player":
                    {
                        if (string.IsNullOrWhiteSpace(playerName))
                        {
                            return "[ERROR] 缺少 player_name 参数。封禁玩家必须指定玩家名称。";
                        }

                        for (int i = bannedPlayersArr.Count - 1; i >= 0; i--)
                        {
                            if (bannedPlayersArr[i] is JsonObject obj)
                            {
                                string existingName = obj["name"]?.GetValue<string>() ?? "";
                                if (string.Equals(existingName, playerName, StringComparison.OrdinalIgnoreCase))
                                {
                                    bannedPlayersArr.RemoveAt(i);
                                }
                            }
                        }

                        string resolvedUuid = ResolvePlayerUuid(serverDir, playerName);
                        bannedPlayersArr.Add(new JsonObject
                        {
                            ["uuid"] = resolvedUuid,
                            ["name"] = playerName,
                            ["created"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss +0000"),
                            ["source"] = source,
                            ["expires"] = expires,
                            ["reason"] = reason
                        });

                        Directory.CreateDirectory(serverDir);
                        File.WriteAllText(bannedPlayersPath, bannedPlayersArr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

                        if (_lifecycleService.IsServerRunning((uint)targetServer.ID))
                        {
                            _consoleService.SendCommand((uint)targetServer.ID, $"ban {playerName} {reason}", true);
                        }

                        await onToolExecuted("manage_bans", new
                        {
                            serverId = targetServer.ID,
                            action = "ban_player",
                            playerName,
                            uuid = resolvedUuid,
                            reason,
                            expires,
                            success = true
                        });

                        return $"[SUCCESS] 已成功封禁玩家 {playerName} (UUID: {resolvedUuid}, 原因: {reason}, 过期: {expires})。";
                    }

                    case "unban_player":
                    {
                        if (string.IsNullOrWhiteSpace(playerName))
                        {
                            return "[ERROR] 缺少 player_name 参数。解封玩家必须指定玩家名称。";
                        }

                        int removedCount = 0;
                        for (int i = bannedPlayersArr.Count - 1; i >= 0; i--)
                        {
                            if (bannedPlayersArr[i] is JsonObject obj)
                            {
                                string existingName = obj["name"]?.GetValue<string>() ?? "";
                                if (string.Equals(existingName, playerName, StringComparison.OrdinalIgnoreCase))
                                {
                                    bannedPlayersArr.RemoveAt(i);
                                    removedCount++;
                                }
                            }
                        }

                        if (removedCount > 0)
                        {
                            File.WriteAllText(bannedPlayersPath, bannedPlayersArr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                            if (_lifecycleService.IsServerRunning((uint)targetServer.ID))
                            {
                                _consoleService.SendCommand((uint)targetServer.ID, $"pardon {playerName}", true);
                            }
                        }

                        await onToolExecuted("manage_bans", new
                        {
                            serverId = targetServer.ID,
                            action = "unban_player",
                            playerName,
                            removedCount,
                            success = removedCount > 0
                        });

                        return removedCount > 0
                            ? $"[SUCCESS] 已成功解封玩家 {playerName}。"
                            : $"[INFO] 封禁名单中未找到玩家 {playerName}。";
                    }

                    case "ban_ip":
                    {
                        if (string.IsNullOrWhiteSpace(ip))
                        {
                            return "[ERROR] 缺少 ip 参数。封禁 IP 必须指定 IP 地址。";
                        }

                        for (int i = bannedIpsArr.Count - 1; i >= 0; i--)
                        {
                            if (bannedIpsArr[i] is JsonObject obj)
                            {
                                string existingIp = obj["ip"]?.GetValue<string>() ?? "";
                                if (string.Equals(existingIp, ip, StringComparison.OrdinalIgnoreCase))
                                {
                                    bannedIpsArr.RemoveAt(i);
                                }
                            }
                        }

                        bannedIpsArr.Add(new JsonObject
                        {
                            ["ip"] = ip,
                            ["created"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss +0000"),
                            ["source"] = source,
                            ["expires"] = expires,
                            ["reason"] = reason
                        });

                        Directory.CreateDirectory(serverDir);
                        File.WriteAllText(bannedIpsPath, bannedIpsArr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

                        if (_lifecycleService.IsServerRunning((uint)targetServer.ID))
                        {
                            _consoleService.SendCommand((uint)targetServer.ID, $"ban-ip {ip} {reason}", true);
                        }

                        await onToolExecuted("manage_bans", new
                        {
                            serverId = targetServer.ID,
                            action = "ban_ip",
                            ip,
                            reason,
                            expires,
                            success = true
                        });

                        return $"[SUCCESS] 已成功封禁 IP: {ip} (原因: {reason}, 过期: {expires})。";
                    }

                    case "unban_ip":
                    {
                        if (string.IsNullOrWhiteSpace(ip))
                        {
                            return "[ERROR] 缺少 ip 参数。解封 IP 必须指定 IP 地址。";
                        }

                        int removedCount = 0;
                        for (int i = bannedIpsArr.Count - 1; i >= 0; i--)
                        {
                            if (bannedIpsArr[i] is JsonObject obj)
                            {
                                string existingIp = obj["ip"]?.GetValue<string>() ?? "";
                                if (string.Equals(existingIp, ip, StringComparison.OrdinalIgnoreCase))
                                {
                                    bannedIpsArr.RemoveAt(i);
                                    removedCount++;
                                }
                            }
                        }

                        if (removedCount > 0)
                        {
                            File.WriteAllText(bannedIpsPath, bannedIpsArr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                            if (_lifecycleService.IsServerRunning((uint)targetServer.ID))
                            {
                                _consoleService.SendCommand((uint)targetServer.ID, $"pardon-ip {ip}", true);
                            }
                        }

                        await onToolExecuted("manage_bans", new
                        {
                            serverId = targetServer.ID,
                            action = "unban_ip",
                            ip,
                            removedCount,
                            success = removedCount > 0
                        });

                        return removedCount > 0
                            ? $"[SUCCESS] 已成功解封 IP: {ip}。"
                            : $"[INFO] 封禁名单中未找到 IP: {ip}。";
                    }

                    default:
                        return $"[ERROR] 未知的 bans 操作类型: {action}";
                }
            }

            default:
                return null;
        }
    }

    private static (McServerInfo.ServerInfo? server, string? error) ResolveTargetServer(JsonObject? args)
    {
        int targetId = 0;
        if (args != null && args.ContainsKey("server_id") && args["server_id"] != null)
        {
            int.TryParse(args["server_id"]!.ToString(), out targetId);
        }

        var serverList = MSLX.SDK.MSLX.Config.Servers.GetServerList();
        var targetServer = targetId > 0
            ? serverList.FirstOrDefault(s => s.ID == targetId)
            : serverList.OrderByDescending(s => s.ID).FirstOrDefault();

        if (targetServer == null)
        {
            return (null, "[ERROR] 未找到目标服务器实例。请先创建或指定正确的 server_id。");
        }

        return (targetServer, null);
    }

    private static string GetServerDirectory(McServerInfo.ServerInfo targetServer)
    {
        string serverDir = targetServer.Base;
        if (string.IsNullOrEmpty(serverDir) || !Directory.Exists(serverDir))
        {
            serverDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Servers", targetServer.ID.ToString());
        }
        return Path.GetFullPath(serverDir);
    }

    private static string ResolvePlayerUuid(string serverDir, string playerName)
    {
        try
        {
            string usercachePath = Path.Combine(serverDir, "usercache.json");
            if (File.Exists(usercachePath))
            {
                string json = File.ReadAllText(usercachePath, Encoding.UTF8);
                var arr = JsonNode.Parse(json)?.AsArray();
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        if (item is JsonObject obj)
                        {
                            string? name = obj["name"]?.GetValue<string>();
                            if (string.Equals(name, playerName, StringComparison.OrdinalIgnoreCase))
                            {
                                string? uuid = obj["uuid"]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(uuid)) return uuid;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return Guid.NewGuid().ToString();
    }

    private sealed class CachedPlayerRecord
    {
        public required string Name { get; init; }
        public required string Uuid { get; init; }
        public string? ExpiresOn { get; init; }
        public DateTime? LastSeen { get; set; }
    }
}
