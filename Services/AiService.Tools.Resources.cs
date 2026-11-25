using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MSLX.SDK;
using MSLX.SDK.IServices;
using MSLX.SDK.Models;
using MSLX.SDK.Models.Files;
using MSLX.SDK.Models.Instance;
using MSLX.SDK.Models.Resources;

namespace MSLX.Plugin.AiAgents.Services;

public partial class AiService
{
    private void RegisterResourcesTools(JsonArray tools)
    {
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "search_mc_resources",
                ["description"] = "在 Modrinth 与 CurseForge 统一资源库中搜索 Minecraft 模组 (Mod)、插件 (Plugin)、整合包 (Modpack)、光影 (Shader)、资源包 (ResourcePack) 或数据包 (DataPack)。支持按游戏版本、加载器 (Fabric, Forge, NeoForge, Paper, Spigot 等) 和关键词精准筛选。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "搜索关键词，如 'jei', 'worldedit', 'fabric api', 'authme'；留空时默认返回热门排行" },
                        ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "Mod", "Plugin", "Modpack", "Shader", "ResourcePack", "DataPack" }, ["description"] = "资源类别（可选）" },
                        ["game_version"] = new JsonObject { ["type"] = "string", ["description"] = "Minecraft 游戏版本筛选，如 '1.20.1', '1.21'（可选）" },
                        ["loader"] = new JsonObject { ["type"] = "string", ["description"] = "加载器筛选，模组如 'Fabric', 'Forge', 'NeoForge', 'Quilt'；插件如 'Paper', 'Spigot', 'Purpur', 'Bukkit'（可选）" },
                        ["provider"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "Modrinth", "CurseForge" }, ["description"] = "资源提供源（默认 Modrinth）" },
                        ["limit"] = new JsonObject { ["type"] = "number", ["description"] = "返回结果条数（1-50，默认 10）" },
                        ["offset"] = new JsonObject { ["type"] = "number", ["description"] = "分页偏移量（默认 0）" }
                    }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "query_resource_versions",
                ["description"] = "查询指定 Minecraft 模组或插件的可用发布版本列表，包含版本号、支持的 MC 版本、对应加载器、文件大小及直链下载地址。在准备下载资源前建议先调用此工具获取精准版本。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["resource_id"] = new JsonObject { ["type"] = "string", ["description"] = "资源唯一标识 ID 或 slug (例如 'fabric-api', 'P7dR8mSH', 'worldedit')" },
                        ["provider"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "Modrinth", "CurseForge" }, ["description"] = "资源源平台（默认 Modrinth）" },
                        ["game_version"] = new JsonObject { ["type"] = "string", ["description"] = "目标 Minecraft 游戏版本号，如 '1.20.1'（可选）" },
                        ["loader"] = new JsonObject { ["type"] = "string", ["description"] = "目标加载器，如 'fabric', 'forge', 'paper', 'spigot'（可选）" }
                    },
                    ["required"] = new JsonArray { "resource_id" }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "download_file_to_server",
                ["description"] = "通过远程 URL 直接高速下载模组 (.jar)、插件 (.jar)、配置文件或整合包文件到指定服务器实例的目录下（智能自动存入 plugins/ 或 mods/，亦支持指定相对目录），并在后台创建全局下载任务跟踪进度。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "目标服务器 ID，未指定时结合当前上下文或最新服务器" },
                        ["url"] = new JsonObject { ["type"] = "string", ["description"] = "文件的完整远程下载 URL 地址" },
                        ["file_name"] = new JsonObject { ["type"] = "string", ["description"] = "目标保存的文件名（如 'EssentialsX-2.20.1.jar'），留空时从 URL 自动推导" },
                        ["destination_folder"] = new JsonObject { ["type"] = "string", ["description"] = "服务器实例内的相对存放子目录，如 'plugins', 'mods', 'config' 或 ''(根目录)；留空时根据核心类型与文件扩展名自动判定" }
                    },
                    ["required"] = new JsonArray { "url" }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "audit_client_only_mods",
                ["description"] = "全面深度体检指定服务器 mods 文件夹下的所有模组 Jar 包，深度解析包内 fabric.mod.json、mods.toml、neoforge.mods.toml 中的 environment/side 声明，智能识别出 OptiFine、Sodium、Iris、ModMenu、Bobby 等客户端独占模组，精准预防服务端启动时 ClassNotFound / 崩溃问题。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "目标服务器 ID，未指定时结合当前上下文" },
                        ["mods_folder"] = new JsonObject { ["type"] = "string", ["description"] = "相对模组目录路径，默认为 'mods'" }
                    }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "manage_mod_plugin_state",
                ["description"] = "管理服务器 mods 或 plugins 目录下的模组/插件状态：支持列出清单 (list)、启用 (enable，将 .jar.disabled 恢复为 .jar)、禁用 (disable，重命名为 .jar.disabled 避免加载崩溃)、彻底删除 (delete，若删除需要用户二次确认授权)。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "目标服务器 ID" },
                        ["folder_type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "mods", "plugins" }, ["description"] = "目标文件夹类型：'mods' 或 'plugins'，留空时根据服务器核心自动判断" },
                        ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "list", "enable", "disable", "delete" }, ["description"] = "操作指令：'list'(列出清单), 'enable'(启用), 'disable'(禁用), 'delete'(删除)" },
                        ["targets"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "目标文件名列表（如 ['OptiFine_1.20.1.jar', 'sodium-fabric-0.5.8.jar']），针对 enable/disable/delete 时传入" }
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
                ["name"] = "query_python_environments",
                ["description"] = "扫描并检测宿主机当前可用的 Python 运行环境及是否安装了 MCDReforged (MCDR)。开服前若用户需要 MCDR 架构服，调用此工具能够快速探测 Python 路径、版本及 MCDR 安装状态。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["force_refresh"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否强制全盘重新扫描（忽略现有缓存，默认 false）" },
                        ["python_path"] = new JsonObject { ["type"] = "string", ["description"] = "可选：检测指定的 Python 可执行文件路径或别名（如 'python', 'python3', 'C:\\Python311\\python.exe'）" }
                    }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "extract_archive",
                ["description"] = "在服务器实例目录下解压指定的压缩归档包（支持 .zip, .tar, .tar.gz, .tgz, .7z, .rar 等格式），可用于一键解压整合包、还原地图存档或批量部署资源包。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "目标服务器 ID" },
                        ["archive_path"] = new JsonObject { ["type"] = "string", ["description"] = "相对服务器目录的压缩包路径，如 'modpack.zip', 'plugins.tar.gz', 'backup.zip'" },
                        ["destination_folder"] = new JsonObject { ["type"] = "string", ["description"] = "解压目标相对目录，默认为 '' (即服务器根目录)" },
                        ["overwrite"] = new JsonObject { ["type"] = "boolean", ["description"] = "遇到同名文件时是否强制覆盖（默认 true）" },
                        ["password"] = new JsonObject { ["type"] = "string", ["description"] = "解压密码（若归档受密码保护）" }
                    },
                    ["required"] = new JsonArray { "archive_path" }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "compress_files",
                ["description"] = "将服务器实例目录下的指定文件或文件夹（如 'world', 'plugins', 'server.properties'）打包压缩为归档文件 (.zip 或 .tar.gz)，便于用户导出、分享或留存快照备份。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "目标服务器 ID" },
                        ["source_paths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "需要打包的相对文件或目录路径列表，如 ['world', 'config', 'server.properties']" },
                        ["output_archive_name"] = new JsonObject { ["type"] = "string", ["description"] = "输出的压缩包相对路径及文件名，如 'world_snapshot.zip' 或 'backups/custom.zip'" },
                        ["archive_type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "zip", "tar.gz" }, ["description"] = "压缩包类型（默认 'zip'）" }
                    },
                    ["required"] = new JsonArray { "source_paths", "output_archive_name" }
                }
            }
        });
    }

    private async Task<string?> HandleResourcesToolAsync(string name, JsonObject? args, Func<string, object, Task> onToolExecuted,
        bool assumeConfirmed, string userId)
    {
        switch (name)
        {
            case "search_mc_resources":
                return await HandleSearchMcResourcesAsync(args, onToolExecuted);

            case "query_resource_versions":
                return await HandleQueryResourceVersionsAsync(args, onToolExecuted);

            case "download_file_to_server":
                return await HandleDownloadFileToServerAsync(args, onToolExecuted, userId, assumeConfirmed);

            case "audit_client_only_mods":
                return await HandleAuditClientOnlyModsAsync(args, onToolExecuted);

            case "manage_mod_plugin_state":
                return await HandleManageModPluginStateAsync(args, onToolExecuted, assumeConfirmed, userId);

            case "query_python_environments":
                return await HandleQueryPythonEnvironmentsAsync(args, onToolExecuted);

            case "extract_archive":
                return await HandleExtractArchiveAsync(args, onToolExecuted, userId, assumeConfirmed);

            case "compress_files":
                return await HandleCompressFilesAsync(args, onToolExecuted, userId, assumeConfirmed);

            default:
                return null;
        }
    }

    #region 1. search_mc_resources

    private async Task<string> HandleSearchMcResourcesAsync(JsonObject? args, Func<string, object, Task> onToolExecuted)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var resourceService = scope.ServiceProvider.GetService<IUnifiedResourceService>()
                ?? _serviceProvider.GetService<IUnifiedResourceService>();

            if (resourceService == null)
            {
                return "[ERROR] 统一资源搜索服务未就绪 (IUnifiedResourceService 未能从容器中解析)。";
            }

            string query = args?["query"]?.ToString() ?? "";
            string? typeStr = args?["type"]?.ToString();
            string? gameVersion = args?["game_version"]?.ToString();
            string? loader = args?["loader"]?.ToString();
            string? providerStr = args?["provider"]?.ToString();

            int limit = 10;
            if (args != null && args.ContainsKey("limit") && int.TryParse(args["limit"]?.ToString(), out int parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 1, 50);
            }

            int offset = 0;
            if (args != null && args.ContainsKey("offset") && int.TryParse(args["offset"]?.ToString(), out int parsedOffset))
            {
                offset = Math.Max(0, parsedOffset);
            }

            var filter = new ResourceSearchFilter
            {
                Query = query,
                GameVersion = gameVersion ?? string.Empty,
                Limit = limit,
                Offset = offset,
                UseMirror = true
            };

            if (!string.IsNullOrWhiteSpace(typeStr) && Enum.TryParse<ResourceType>(typeStr, true, out var rType))
            {
                filter.Type = rType;
            }

            if (!string.IsNullOrWhiteSpace(providerStr) && Enum.TryParse<ResourceProviderType>(providerStr, true, out var rProvider))
            {
                filter.Provider = rProvider;
            }

            if (!string.IsNullOrWhiteSpace(loader))
            {
                var lower = loader.ToLowerInvariant();
                if (lower is "paper" or "spigot" or "purpur" or "bukkit" or "velocity" or "bungeecord" or "waterfall")
                {
                    filter.PluginLoaders = [loader];
                }
                else
                {
                    filter.GameLoaders = [loader];
                }
            }

            var searchResult = await resourceService.SearchAsync(filter);
            var items = searchResult?.Items?.ToList() ?? new List<Resource>();

            var summaryList = items.Select(h => new
            {
                id = h.Id,
                name = h.Name,
                author = h.Author,
                summary = !string.IsNullOrWhiteSpace(h.TranslatedSummary) ? h.TranslatedSummary : h.Summary,
                provider = h.Provider.ToString(),
                downloads = h.DownloadCount,
                updatedAt = h.UpdatedAt.ToString("yyyy-MM-dd")
            }).ToList();

            await onToolExecuted("search_mc_resources", new
            {
                query,
                type = filter.Type?.ToString(),
                provider = filter.Provider?.ToString(),
                totalCount = searchResult?.TotalCount ?? items.Count,
                resultsCount = summaryList.Count,
                items = summaryList
            });

            if (summaryList.Count == 0)
            {
                return $"[SEARCH_RESULT] 未检索到符合条件的资源（关键词: '{query}', 类型: {filter.Type}, 版本: {gameVersion ?? "全部"}）。建议更换关键词或放宽过滤条件。";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[SUCCESS] 成功搜索到 {searchResult?.TotalCount ?? items.Count} 个相关资源（当前呈现前 {summaryList.Count} 条）：");
            for (int i = 0; i < summaryList.Count; i++)
            {
                var item = summaryList[i];
                sb.AppendLine($"{i + 1}. **{item.name}** (`ID: {item.id}`, 平台: {item.provider})");
                sb.AppendLine($"   - 作者: {item.author} | 下载量: {item.downloads:N0} | 更新时间: {item.updatedAt}");
                if (!string.IsNullOrWhiteSpace(item.summary))
                {
                    sb.AppendLine($"   - 简介: {item.summary}");
                }
            }
            sb.AppendLine("\n提示：若需下载某一资源，请先调用 `query_resource_versions` 查询其版本与下载直链。");

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleSearchMcResourcesAsync 异常: {Message}", ex.Message);
            return $"[ERROR] 资源搜索失败: {ex.Message}";
        }
    }

    #endregion

    #region 2. query_resource_versions

    private async Task<string> HandleQueryResourceVersionsAsync(JsonObject? args, Func<string, object, Task> onToolExecuted)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var resourceService = scope.ServiceProvider.GetService<IUnifiedResourceService>()
                ?? _serviceProvider.GetService<IUnifiedResourceService>();

            if (resourceService == null)
            {
                return "[ERROR] 统一资源搜索服务未就绪 (IUnifiedResourceService 未能从容器中解析)。";
            }

            string resourceId = args?["resource_id"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                return "[ERROR] 参数 resource_id 不能为空。";
            }

            string? providerStr = args?["provider"]?.ToString();
            var providerType = ResourceProviderType.Modrinth;
            if (!string.IsNullOrWhiteSpace(providerStr) && Enum.TryParse<ResourceProviderType>(providerStr, true, out var p))
            {
                providerType = p;
            }

            string? gameVersion = args?["game_version"]?.ToString();
            string? loader = args?["loader"]?.ToString();

            var versions = await resourceService.GetVersionsAsync(resourceId, providerType, gameVersion ?? string.Empty, loader ?? string.Empty, useMirror: true);
            var versionList = versions?.ToList() ?? new List<ResourceVersion>();

            var displayList = versionList.Take(10).Select(v => new
            {
                id = v.Id,
                name = v.Name,
                versionNumber = v.VersionNumber,
                gameVersions = v.GameVersions ?? new List<string>(),
                loaders = v.Loaders ?? new List<string>(),
                filename = v.Filename,
                size = FormatBytes(v.FileSizeBytes),
                environment = v.Environment == 1 ? "仅服务端" : (v.Environment == 0 ? "仅客户端" : "双端通用"),
                downloadUrl = v.DownloadUrl
            }).ToList();

            await onToolExecuted("query_resource_versions", new
            {
                resourceId,
                provider = providerType.ToString(),
                totalVersions = versionList.Count,
                items = displayList
            });

            if (displayList.Count == 0)
            {
                return $"[VERSION_RESULT] 未能获取到资源 `{resourceId}` 的发布版本（筛选：游戏版本={gameVersion ?? "不限"}, 加载器={loader ?? "不限"}）。";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[SUCCESS] 资源 `{resourceId}` ({providerType}) 共找到 {versionList.Count} 个可用版本（展示前 {displayList.Count} 个最新版本）：");
            for (int i = 0; i < displayList.Count; i++)
            {
                var v = displayList[i];
                sb.AppendLine($"{i + 1}. **{v.name}** (`版本: {v.versionNumber}`, 文件: `{v.filename}`)");
                sb.AppendLine($"   - 支持 MC: [{string.Join(", ", v.gameVersions.Take(5))}] | 加载器: [{string.Join(", ", v.loaders)}] | 环境: {v.environment} | 大小: {v.size}");
                sb.AppendLine($"   - 下载直链: {v.downloadUrl}");
            }
            sb.AppendLine("\n提示：可直接调用 `download_file_to_server` 传入上述直链将文件下载部署到指定服务器。");

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleQueryResourceVersionsAsync 异常: {Message}", ex.Message);
            return $"[ERROR] 获取资源版本失败: {ex.Message}";
        }
    }

    #endregion

    #region 3. download_file_to_server

    private async Task<string> HandleDownloadFileToServerAsync(JsonObject? args, Func<string, object, Task> onToolExecuted, string userId, bool assumeConfirmed)
    {
        string? temporaryFile = null;
        string? taskId = null;
        try
        {
            string url = args?["url"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(url))
            {
                return "[ERROR] 下载地址 url 不能为空。";
            }

            var (targetServer, safeBaseDir, err) = ResolveServerAndBaseDir(args);
            if (err != null || targetServer == null)
            {
                return err ?? "[ERROR] 未能解析服务器实例。";
            }

            // 智能推断或使用指定的存放目录
            string? destinationFolder = args?["destination_folder"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(destinationFolder))
            {
                string core = (targetServer.Core ?? "").ToLowerInvariant();
                bool isPluginCore = core.Contains("paper") || core.Contains("spigot") || core.Contains("purpur") || core.Contains("bukkit");
                bool isModCore = core.Contains("forge") || core.Contains("fabric") || core.Contains("neoforge") || core.Contains("quilt");

                if (isPluginCore)
                {
                    destinationFolder = "plugins";
                }
                else if (isModCore)
                {
                    destinationFolder = "mods";
                }
                else
                {
                    if (Directory.Exists(Path.Combine(safeBaseDir, "plugins"))) destinationFolder = "plugins";
                    else if (Directory.Exists(Path.Combine(safeBaseDir, "mods"))) destinationFolder = "mods";
                    else destinationFolder = "plugins";
                }
            }

            string relativeFolder = destinationFolder.TrimStart('/', '\\');
            string targetDir = Path.GetFullPath(Path.Combine(safeBaseDir, relativeFolder));
            if (!IsWithinServerDirectory(safeBaseDir, targetDir))
            {
                return "[ERROR] 越权操作：保存目录超出服务器根路径。";
            }
            Directory.CreateDirectory(targetDir);

            // 推断文件名
            string? fileName = args?["file_name"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                try
                {
                    var uri = new Uri(url);
                    fileName = Path.GetFileName(uri.AbsolutePath);
                }
                catch { }

                if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    fileName = $"download_{DateTime.Now:yyyyMMddHHmmss}.jar";
                }
            }

            string finalFilePath = Path.GetFullPath(Path.Combine(targetDir, fileName));
            if (!IsWithinServerDirectory(safeBaseDir, finalFilePath))
            {
                return "[ERROR] 越权操作：目标文件路径超出服务器根路径。";
            }

            string displayRelPath = Path.GetRelativePath(safeBaseDir, finalFilePath);

            if (!assumeConfirmed && File.Exists(finalFilePath))
            {
                // Freeze the selected name so approval executes the displayed destination.
                args!["file_name"] = fileName;
                return await RequestReplacementAsync("download_file_to_server", args.ToJsonString(),
                    targetServer.ID, displayRelPath, userId, onToolExecuted);
            }
            temporaryFile = Path.Combine(targetDir, $".mslx-download-{Guid.NewGuid():N}.tmp");

            // 注册全局后台任务 (若任务管理器可用)
            try
            {
                if (MSLX.SDK.MSLX.Tasks != null)
                {
                    var (taskItem, _) = MSLX.SDK.MSLX.Tasks.CreateTask(
                        string.IsNullOrEmpty(userId) ? "ai-agent" : userId,
                        (uint)targetServer.ID,
                        TaskType.Download,
                        $"AI 下载: {fileName}",
                        fileName);
                    taskId = taskItem.Id;
                    MSLX.SDK.MSLX.Tasks.UpdateProgress(taskId, 5, "正在连接并下载文件...", TaskState.Running);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "创建后台下载任务异常，将降级执行: {Message}", ex.Message);
            }

            bool success = false;
            string? errorMsg = null;
            long downloadedBytes = 0;

            // 优先尝试使用 MSLX SDK 官方下载器服务
            if (MSLX.SDK.MSLX.Downloader != null)
            {
                var dlResult = await MSLX.SDK.MSLX.Downloader.DownloadFileAsync(url, temporaryFile, (prog, speed) =>
                {
                    if (taskId != null)
                    {
                        MSLX.SDK.MSLX.Tasks?.UpdateProgress(taskId, (int)prog, $"下载中... {prog:F1}% ({speed})");
                    }
                });
                success = dlResult.Success;
                errorMsg = dlResult.ErrorMessage;
            }
            else
            {
                // 降级使用内置 HttpClient
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? 0;
                await using var fs = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var contentStream = await response.Content.ReadAsStreamAsync();

                var buffer = new byte[81920];
                int read;
                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, read);
                    downloadedBytes += read;
                    if (taskId != null && totalBytes > 0)
                    {
                        int pct = (int)((downloadedBytes * 100) / totalBytes);
                        MSLX.SDK.MSLX.Tasks?.UpdateProgress(taskId, pct, $"下载中... {pct}%");
                    }
                }
                success = true;
            }

            if (success)
            {
                File.Move(temporaryFile, finalFilePath, overwrite: assumeConfirmed);
                if (taskId != null)
                {
                    MSLX.SDK.MSLX.Tasks?.SetSuccess(taskId, $"已成功下载至 {displayRelPath}");
                }

                long sizeBytes = File.Exists(finalFilePath) ? new FileInfo(finalFilePath).Length : downloadedBytes;
                var resultData = new
                {
                    serverId = targetServer.ID,
                    fileName,
                    relativePath = displayRelPath,
                    size = FormatBytes(sizeBytes),
                    success = true
                };

                await onToolExecuted("download_file_to_server", resultData);
                return $"[SUCCESS] 成功下载文件 `{fileName}` ({FormatBytes(sizeBytes)}) 到服务器 {targetServer.ID} 的 `{displayRelPath}`！";
            }
            else
            {
                if (taskId != null)
                {
                    MSLX.SDK.MSLX.Tasks?.SetFailed(taskId, errorMsg ?? "文件下载失败");
                }
                return $"[ERROR] 下载文件失败: {errorMsg ?? "未知网络异常"}";
            }
        }
        catch (Exception ex)
        {
            if (taskId != null) MSLX.SDK.MSLX.Tasks?.SetFailed(taskId, "下载文件无法写入目标位置。");
            _logger.LogError(ex, "下载文件失败");
            return $"[ERROR] 下载文件异常: {ex.Message}";
        }
        finally
        {
            if (temporaryFile != null) File.Delete(temporaryFile);
        }
    }

    #endregion

    #region 4. audit_client_only_mods

    private async Task<string> HandleAuditClientOnlyModsAsync(JsonObject? args, Func<string, object, Task> onToolExecuted)
    {
        try
        {
            var (targetServer, safeBaseDir, err) = ResolveServerAndBaseDir(args);
            if (err != null || targetServer == null)
            {
                return err ?? "[ERROR] 未能解析服务器实例。";
            }

            string relativeFolder = args?["mods_folder"]?.ToString()?.Trim() ?? "mods";
            string modsDir = Path.GetFullPath(Path.Combine(safeBaseDir, relativeFolder));

            if (!IsWithinServerDirectory(safeBaseDir, modsDir) || !Directory.Exists(modsDir))
            {
                return $"[INFO] 服务器 {targetServer.ID} 的模组目录 `{relativeFolder}` 不存在或尚未创建模组。";
            }

            var jarFiles = Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(modsDir, "*.jar.disabled", SearchOption.TopDirectoryOnly))
                .ToList();

            if (jarFiles.Count == 0)
            {
                return $"[INFO] 服务器模组目录 `{relativeFolder}` 内暂未发现任何模组 (.jar) 文件。";
            }

            var clientOnlyReport = new List<ClientModAuditItem>();
            int healthyCount = 0;

            // 知名客户端独占模组文件名与特征库
            var knownClientModKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "optifine", "sodium", "rubidium", "embeddium", "iris", "oculus",
                "modmenu", "bobby", "continuity", "indium", "freecam", "replaymod",
                "betterf3", "minihud", "litematica", "tweakeroo", "soundphysics",
                "customskinloader", "chatheads", "presencefootsteps", "inventoryprofilesnext",
                "mousetweaks", "controlling", "searchables", "entityculling", "ferritecore-client"
            };

            foreach (var jarPath in jarFiles)
            {
                string fileName = Path.GetFileName(jarPath);
                bool isDisabled = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string cleanName = isDisabled ? fileName.Substring(0, fileName.Length - ".disabled".Length) : fileName;

                bool detected = false;
                string reason = string.Empty;
                string modId = string.Empty;
                string modName = cleanName;

                // 1. 尝试打开 Jar 包分析内部元数据
                try
                {
                    using var stream = new FileStream(jarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

                    // 1.1 Fabric/Quilt 元数据检查
                    var fabricEntry = archive.GetEntry("fabric.mod.json") ?? archive.GetEntry("quilt.mod.json");
                    if (fabricEntry != null)
                    {
                        using var feStream = fabricEntry.Open();
                        using var doc = JsonDocument.Parse(feStream);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("id", out var idProp)) modId = idProp.GetString() ?? "";
                        if (root.TryGetProperty("name", out var nameProp)) modName = nameProp.GetString() ?? cleanName;

                        if (root.TryGetProperty("environment", out var envProp))
                        {
                            string envVal = envProp.GetString() ?? "";
                            if (string.Equals(envVal, "client", StringComparison.OrdinalIgnoreCase))
                            {
                                detected = true;
                                reason = "fabric.mod.json 明确声明 `environment: \"client\"` (纯客户端运行，无法在服务端运行)";
                            }
                        }
                    }

                    // 1.2 Forge / NeoForge mods.toml 检查
                    if (!detected)
                    {
                        var tomlEntry = archive.GetEntry("META-INF/neoforge.mods.toml") ?? archive.GetEntry("META-INF/mods.toml");
                        if (tomlEntry != null)
                        {
                            using var teStream = tomlEntry.Open();
                            using var reader = new StreamReader(teStream, Encoding.UTF8);
                            string tomlContent = reader.ReadToEnd();

                            if (Regex.IsMatch(tomlContent, @"displayTest\s*=\s*""IGNORE_SERVER_VERSION""", RegexOptions.IgnoreCase) ||
                                Regex.IsMatch(tomlContent, @"side\s*=\s*""CLIENT""", RegexOptions.IgnoreCase) ||
                                Regex.IsMatch(tomlContent, @"clientOnly\s*=\s*true", RegexOptions.IgnoreCase))
                            {
                                detected = true;
                                reason = "mods.toml 配置中声明为客户端单侧/忽略服务端匹配 (side=CLIENT 或 displayTest=IGNORE_SERVER_VERSION)";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "读取模组 Jar 包元数据跳过 {FileName}: {Message}", fileName, ex.Message);
                }

                // 1.3 特征名命中回退
                if (!detected)
                {
                    foreach (var kw in knownClientModKeywords)
                    {
                        if (cleanName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            detected = true;
                            reason = $"文件名命中客户端独占/优化渲染类模组特征库 `{kw}` (如 OptiFine/光影/HUD/小地图/客户端UI等)";
                            break;
                        }
                    }
                }

                if (detected)
                {
                    clientOnlyReport.Add(new ClientModAuditItem
                    {
                        FileName = fileName,
                        ModId = modId,
                        ModName = modName,
                        IsDisabled = isDisabled,
                        Reason = reason,
                        Recommendation = isDisabled
                            ? "当前已处于禁用状态 (.disabled)，安全无需操作。"
                            : "此模组在服务端极易引发 NoClassDefFoundError 崩溃！强烈建议调用 manage_mod_plugin_state 执行禁用 (.disabled) 或删除。"
                    });
                }
                else
                {
                    healthyCount++;
                }
            }

            var auditResultData = new
            {
                serverId = targetServer.ID,
                scannedCount = jarFiles.Count,
                clientOnlyCount = clientOnlyReport.Count,
                healthyCount,
                clientOnlyMods = clientOnlyReport
            };

            await onToolExecuted("audit_client_only_mods", auditResultData);

            var sb = new StringBuilder();
            sb.AppendLine($"[AUDIT_SUMMARY] 模组客户端兼容性体检完成（共扫描 {jarFiles.Count} 个 Jar 文件）：");
            sb.AppendLine($"- 正常/双端模组: {healthyCount} 个");
            sb.AppendLine($"- 发现客户端独占模组: {clientOnlyReport.Count} 个\n");

            if (clientOnlyReport.Count == 0)
            {
                sb.AppendLine("🎉 恭喜！未检测到任何客户端独占模组，模组库在服务端兼容性良好，可放心开服。");
            }
            else
            {
                sb.AppendLine("⚠️ **以下模组被确认为【客户端独占模组】，在专用服务端加载将导致崩服**：");
                for (int i = 0; i < clientOnlyReport.Count; i++)
                {
                    var item = clientOnlyReport[i];
                    string statusTag = item.IsDisabled ? "【已禁用】" : "【⚠️运行中】";
                    sb.AppendLine($"{i + 1}. {statusTag} `{item.FileName}` ({item.ModName})");
                    sb.AppendLine($"   - 检出依据: {item.Reason}");
                    sb.AppendLine($"   - 建议处置: {item.Recommendation}");
                }
                sb.AppendLine("\n💡 处置建议：可立即对活跃的客户端模组调用 `manage_mod_plugin_state` 传入 targets 并选择 `disable` 将其安全禁用。");
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleAuditClientOnlyModsAsync 异常: {Message}", ex.Message);
            return $"[ERROR] 模组客户端体检失败: {ex.Message}";
        }
    }

    private sealed class ClientModAuditItem
    {
        public string FileName { get; set; } = string.Empty;
        public string ModId { get; set; } = string.Empty;
        public string ModName { get; set; } = string.Empty;
        public bool IsDisabled { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Recommendation { get; set; } = string.Empty;
    }

    #endregion

    #region 5. manage_mod_plugin_state

    private async Task<string> HandleManageModPluginStateAsync(JsonObject? args, Func<string, object, Task> onToolExecuted,
        bool assumeConfirmed, string userId)
    {
        try
        {
            var (targetServer, safeBaseDir, err) = ResolveServerAndBaseDir(args);
            if (err != null || targetServer == null)
            {
                return err ?? "[ERROR] 未能解析服务器实例。";
            }

            string action = args?["action"]?.ToString()?.Trim().ToLowerInvariant() ?? "";
            if (string.IsNullOrWhiteSpace(action))
            {
                return "[ERROR] 参数 action 不能为空 (可选: list, enable, disable, delete)。";
            }

            // 确定目标目录 (mods 或 plugins)
            string? folderType = args?["folder_type"]?.ToString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(folderType))
            {
                string core = (targetServer.Core ?? "").ToLowerInvariant();
                if (core.Contains("paper") || core.Contains("spigot") || core.Contains("purpur") || core.Contains("bukkit"))
                {
                    folderType = "plugins";
                }
                else if (core.Contains("forge") || core.Contains("fabric") || core.Contains("neoforge") || core.Contains("quilt"))
                {
                    folderType = "mods";
                }
                else
                {
                    folderType = Directory.Exists(Path.Combine(safeBaseDir, "plugins")) ? "plugins" : "mods";
                }
            }

            string targetDir = Path.GetFullPath(Path.Combine(safeBaseDir, folderType));
            if (!IsWithinServerDirectory(safeBaseDir, targetDir))
            {
                return "[ERROR] 越权操作：目标目录超出服务器根路径。";
            }

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 1. LIST 清单
            if (action == "list")
            {
                var files = Directory.GetFiles(targetDir, "*.jar", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(targetDir, "*.jar.disabled", SearchOption.TopDirectoryOnly))
                    .OrderBy(f => Path.GetFileName(f))
                    .ToList();

                var listData = files.Select(f =>
                {
                    var fi = new FileInfo(f);
                    bool isDisabled = f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    return new
                    {
                        fileName = fi.Name,
                        enabled = !isDisabled,
                        size = FormatBytes(fi.Length),
                        lastModified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                    };
                }).ToList();

                await onToolExecuted("manage_mod_plugin_state", new
                {
                    serverId = targetServer.ID,
                    folderType,
                    action = "list",
                    totalCount = listData.Count,
                    items = listData
                });

                if (listData.Count == 0)
                {
                    return $"[INFO] 服务器 {targetServer.ID} 的 `{folderType}` 目录下暂无任何模组/插件文件。";
                }

                var sb = new StringBuilder();
                sb.AppendLine($"[LIST] 服务器 {targetServer.ID} 的 `{folderType}` 目录文件清单（共 {listData.Count} 项）：");
                for (int i = 0; i < listData.Count; i++)
                {
                    var item = listData[i];
                    string stateTag = item.enabled ? "✅ [启用]" : "⏸️ [已禁用]";
                    sb.AppendLine($"{i + 1}. {stateTag} `{item.fileName}` ({item.size}) - {item.lastModified}");
                }
                return sb.ToString().TrimEnd();
            }

            // 解析操作目标列表
            var targets = new List<string>();
            if (args != null && args.ContainsKey("targets") && args["targets"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item != null && !string.IsNullOrWhiteSpace(item.ToString()))
                    {
                        targets.Add(item.ToString().Trim());
                    }
                }
            }
            else if (args != null && args.ContainsKey("targets") && args["targets"] != null)
            {
                string raw = args["targets"]!.ToString();
                targets.AddRange(raw.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            if (targets.Count == 0)
            {
                return $"[ERROR] 执行 `{action}` 操作时，必须提供 targets 目标文件列表。";
            }

            if (targets.Any(target => target is "." or ".." || target.Contains('/') || target.Contains('\\') ||
                target.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                return "[ERROR] 模组或插件目标必须是当前目录内的文件名。";

            // 2. DELETE 删除操作 (破坏性操作二次授权拦截)
            if (action == "delete")
            {
                if (!assumeConfirmed)
                {


                    string targetSummary = string.Join(", ", targets);
                    string confirmationId = CreatePendingOperation(
                        "manage_mod_plugin_state",
                        args?.ToJsonString() ?? "{}",
                        targetServer.ID,
                        $"{folderType}/[{targetSummary}]",
                        "delete", userId);

                    var confirmData = new
                    {
                        serverId = targetServer.ID,
                        folderType,
                        action = "delete",
                        targets,
                        requiresConfirmation = true,
                        confirmed = false,
                        confirmationId
                    };

                    await onToolExecuted("manage_mod_plugin_state", confirmData);
                    return PendingMessage(confirmationId);
                }

                // 用户已确认删除
                var deleted = new List<string>();
                var notFound = new List<string>();

                foreach (var target in targets)
                {
                    string path1 = Path.Combine(targetDir, target);
                    string path2 = Path.Combine(targetDir, target + ".disabled");

                    if (File.Exists(path1))
                    {
                        File.Delete(path1);
                        deleted.Add(target);
                    }
                    else if (File.Exists(path2))
                    {
                        File.Delete(path2);
                        deleted.Add(target + ".disabled");
                    }
                    else
                    {
                        notFound.Add(target);
                    }
                }

                await onToolExecuted("manage_mod_plugin_state", new
                {
                    serverId = targetServer.ID,
                    folderType,
                    action = "delete",
                    confirmed = true,
                    deleted,
                    notFound
                });

                return $"[SUCCESS] 已彻底删除目标文件: [{string.Join(", ", deleted)}]" +
                       (notFound.Count > 0 ? $" (未找到: [{string.Join(", ", notFound)}])" : "");
            }

            // 3. ENABLE 启用操作 (将 .disabled 重命名为 .jar)
            if (action == "enable")
            {
                var enabledList = new List<string>();
                var failedList = new List<string>();

                foreach (var target in targets)
                {
                    string sourceFile = target;
                    if (!sourceFile.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        sourceFile = target + ".disabled";
                    }

                    string sourcePath = Path.Combine(targetDir, sourceFile);
                    if (!File.Exists(sourcePath))
                    {
                        // 检查是否本身就是已启用的 .jar
                        string normalJar = Path.Combine(targetDir, target.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? target : target + ".jar");
                        if (File.Exists(normalJar))
                        {
                            enabledList.Add($"{Path.GetFileName(normalJar)} (已是启用状态)");
                            continue;
                        }

                        failedList.Add($"{target} (未找到 .disabled 禁用文件)");
                        continue;
                    }

                    string destFile = sourceFile.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
                        ? sourceFile.Substring(0, sourceFile.Length - ".disabled".Length)
                        : (sourceFile.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                            ? sourceFile.Substring(0, sourceFile.Length - ".disabled".Length)
                            : sourceFile);

                    string destPath = Path.Combine(targetDir, destFile);
                    if (File.Exists(destPath))
                    {
                        File.Delete(destPath);
                    }
                    File.Move(sourcePath, destPath);
                    enabledList.Add(destFile);
                }

                await onToolExecuted("manage_mod_plugin_state", new
                {
                    serverId = targetServer.ID,
                    folderType,
                    action = "enable",
                    enabled = enabledList,
                    failed = failedList
                });

                return $"[SUCCESS] 启用模组/插件操作完成：\n- 成功启用: [{string.Join(", ", enabledList)}]\n" +
                       (failedList.Count > 0 ? $"- 失败项: [{string.Join(", ", failedList)}]\n" : "") +
                       "提示：重启服务器后已启用的插件/模组即可正常生效。";
            }

            // 4. DISABLE 禁用操作 (将 .jar 重命名为 .jar.disabled)
            if (action == "disable")
            {
                var disabledList = new List<string>();
                var failedList = new List<string>();

                foreach (var target in targets)
                {
                    string sourceFile = target;
                    if (sourceFile.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        disabledList.Add($"{sourceFile} (已是禁用状态)");
                        continue;
                    }

                    string sourcePath = Path.Combine(targetDir, sourceFile);
                    if (!File.Exists(sourcePath) && !sourceFile.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    {
                        sourceFile += ".jar";
                        sourcePath = Path.Combine(targetDir, sourceFile);
                    }

                    if (!File.Exists(sourcePath))
                    {
                        failedList.Add($"{target} (未找到目标文件)");
                        continue;
                    }

                    string destPath = sourcePath + ".disabled";
                    if (File.Exists(destPath))
                    {
                        File.Delete(destPath);
                    }
                    File.Move(sourcePath, destPath);
                    disabledList.Add(Path.GetFileName(destPath));
                }

                await onToolExecuted("manage_mod_plugin_state", new
                {
                    serverId = targetServer.ID,
                    folderType,
                    action = "disable",
                    disabled = disabledList,
                    failed = failedList
                });

                return $"[SUCCESS] 禁用模组/插件操作完成：\n- 成功禁用: [{string.Join(", ", disabledList)}]\n" +
                       (failedList.Count > 0 ? $"- 失败项: [{string.Join(", ", failedList)}]\n" : "") +
                       "提示：重启服务器后被禁用的插件/模组将不会再被加载，可有效隔离排查崩溃。";
            }

            return $"[ERROR] 未知的 action: {action}。支持: list, enable, disable, delete。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleManageModPluginStateAsync 异常: {Message}", ex.Message);
            return $"[ERROR] 模组/插件状态管理失败: {ex.Message}";
        }
    }

    #endregion

    #region 6. query_python_environments

    private async Task<string> HandleQueryPythonEnvironmentsAsync(JsonObject? args, Func<string, object, Task> onToolExecuted)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var pythonScanner = scope.ServiceProvider.GetService<IPythonScannerService>()
                ?? _serviceProvider.GetService<IPythonScannerService>();

            if (pythonScanner == null)
            {
                return "[ERROR] Python 扫描服务未就绪 (IPythonScannerService 未在容器中注册)。";
            }

            bool forceRefresh = false;
            if (args != null && args.ContainsKey("force_refresh") && bool.TryParse(args["force_refresh"]?.ToString(), out bool fr))
            {
                forceRefresh = fr;
            }

            string? specificPython = args?["python_path"]?.ToString()?.Trim();

            if (!string.IsNullOrWhiteSpace(specificPython))
            {
                var inspectResult = await pythonScanner.InspectPythonAsync(specificPython);
                if (inspectResult == null)
                {
                    return $"[INSPECT_RESULT] 指定的 Python 路径 `{specificPython}` 无效或不可执行。";
                }

                await onToolExecuted("query_python_environments", new
                {
                    inspectedPath = specificPython,
                    result = inspectResult
                });

                return $"[SUCCESS] Python 环境检测结果：\n" +
                       $"- 路径: `{inspectResult.Path}`\n" +
                       $"- 版本: Python {inspectResult.Version}\n" +
                       $"- MCDReforged (MCDR): {(inspectResult.HasMcdr ? $"已安装 ({inspectResult.McdrVersion ?? "已安装"})" : "未安装")}";
            }

            var scannedList = await pythonScanner.ScanPythonAsync(forceRefresh);
            var results = scannedList ?? new List<PythonInfo>();

            await onToolExecuted("query_python_environments", new
            {
                total = results.Count,
                items = results
            });

            if (results.Count == 0)
            {
                return "[INFO] 宿主机暂未检测到任何可用的 Python 环境。若需创建 MCDR 架构服，请指导用户先在系统安装 Python 3.9+。";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[SUCCESS] 宿主机共检测到 {results.Count} 个可用 Python 环境：");
            for (int i = 0; i < results.Count; i++)
            {
                var p = results[i];
                string mcdrTag = p.HasMcdr ? $"✅ [MCDR 已就绪: {p.McdrVersion ?? "OK"}]" : "❌ [无 MCDR]";
                sb.AppendLine($"{i + 1}. **Python {p.Version}** - {mcdrTag}");
                sb.AppendLine($"   - 可执行路径: `{p.Path}`");
            }
            sb.AppendLine("\n提示：部署 MCDR 架构服时，可在 `create_mc_server` 中传入 `mcdr_python: \"<上述路径>\"` 快速开服。");

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleQueryPythonEnvironmentsAsync 异常: {Message}", ex.Message);
            return $"[ERROR] 查询 Python 环境失败: {ex.Message}";
        }
    }

    #endregion

    #region 7. extract_archive

    private async Task<string> HandleExtractArchiveAsync(JsonObject? args, Func<string, object, Task> onToolExecuted, string userId, bool assumeConfirmed)
    {
        try
        {
            var (targetServer, safeBaseDir, err) = ResolveServerAndBaseDir(args);
            if (err != null || targetServer == null)
            {
                return err ?? "[ERROR] 未能解析服务器实例。";
            }

            string archivePath = args?["archive_path"]?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                return "[ERROR] 压缩包路径 archive_path 不能为空。";
            }

            string fullArchivePath = Path.GetFullPath(Path.Combine(safeBaseDir, archivePath));
            if (!IsWithinServerDirectory(safeBaseDir, fullArchivePath) || !File.Exists(fullArchivePath))
            {
                return $"[ERROR] 压缩文件不存在或超出服务器目录: `{archivePath}`";
            }

            string destinationFolder = args?["destination_folder"]?.ToString()?.Trim() ?? "";
            string extractTargetDir = Path.GetFullPath(Path.Combine(safeBaseDir, destinationFolder));
            if (!IsWithinServerDirectory(safeBaseDir, extractTargetDir))
            {
                return "[ERROR] 越权操作：解压目标目录超出服务器根路径。";
            }

            bool overwrite = true;
            if (args != null && args.ContainsKey("overwrite") && bool.TryParse(args["overwrite"]?.ToString(), out bool ow))
            {
                overwrite = ow;
            }

            if (overwrite && !assumeConfirmed && Directory.Exists(extractTargetDir) &&
                Directory.EnumerateFileSystemEntries(extractTargetDir).Any())
                return await RequestReplacementAsync("extract_archive", args?.ToJsonString() ?? "{}",
                    targetServer.ID, Path.GetRelativePath(safeBaseDir, extractTargetDir), userId, onToolExecuted);

            Directory.CreateDirectory(extractTargetDir);
            bool allowOverwrite = overwrite && assumeConfirmed;
            Func<string, bool> isPathSafe = path => allowOverwrite || !File.Exists(path);

            string? password = args?["password"]?.ToString();

            // 注册全局任务跟踪
            string? taskId = null;
            try
            {
                if (MSLX.SDK.MSLX.Tasks != null)
                {
                    var (taskItem, _) = MSLX.SDK.MSLX.Tasks.CreateTask(
                        string.IsNullOrEmpty(userId) ? "ai-agent" : userId,
                        (uint)targetServer.ID,
                        TaskType.Decompress,
                        $"解压归档: {Path.GetFileName(fullArchivePath)}",
                        Path.GetFileName(fullArchivePath));
                    taskId = taskItem.Id;
                    MSLX.SDK.MSLX.Tasks.UpdateProgress(taskId, 10, "正在解压缩...", TaskState.Running);
                }
            }
            catch { }

            string fileNameLower = fullArchivePath.ToLowerInvariant();
            bool extracted = false;
            string? extractError = null;

            // 尝试通过反射调用宿主 ArchiveService (以获得 7z/rar/tar 最佳支持)
            try
            {
                var archiveServiceType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "MSLX.Daemon.Services.ArchiveService");

                if (archiveServiceType != null)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var archiveService = scope.ServiceProvider.GetService(archiveServiceType)
                        ?? _serviceProvider.GetService(archiveServiceType);

                    if (archiveService != null)
                    {
                        var method = archiveServiceType.GetMethod("DecompressAsync", new[]
                        {
                            typeof(string), typeof(string), typeof(string), typeof(string),
                            typeof(CancellationToken), typeof(Action<int, string>), typeof(Func<string, bool>)
                        });

                        if (method != null)
                        {
                            Action<int, string> onProgress = (prog, msg) =>
                            {
                                if (taskId != null) MSLX.SDK.MSLX.Tasks?.UpdateProgress(taskId, prog, msg);
                            };

                            var taskResult = method.Invoke(archiveService, new object?[]
                            {
                                fullArchivePath, extractTargetDir, null, password, CancellationToken.None, onProgress, isPathSafe
                            });

                            if (taskResult is Task t)
                            {
                                await t;
                                extracted = true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "调用宿主 ArchiveService 解压失败，将尝试内置 BCL 解压: {Message}", ex.Message);
            }

            // 降级使用标准库 BCL 解压
            if (!extracted)
            {
                try
                {
                    if (fileNameLower.EndsWith(".zip") || fileNameLower.EndsWith(".jar"))
                    {
                        ZipFile.ExtractToDirectory(fullArchivePath, extractTargetDir, allowOverwrite);
                        extracted = true;
                    }
                    else if (fileNameLower.EndsWith(".tar.gz") || fileNameLower.EndsWith(".tgz"))
                    {
                        await using var fs = File.OpenRead(fullArchivePath);
                        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                        await TarFile.ExtractToDirectoryAsync(gz, extractTargetDir, allowOverwrite);
                        extracted = true;
                    }
                    else if (fileNameLower.EndsWith(".tar"))
                    {
                        await TarFile.ExtractToDirectoryAsync(fullArchivePath, extractTargetDir, allowOverwrite);
                        extracted = true;
                    }
                    else
                    {
                        extractError = $"当前环境不支持该格式直接解压: {Path.GetFileName(fullArchivePath)}";
                    }
                }
                catch (Exception ex)
                {
                    extractError = ex.Message;
                }
            }

            if (extracted)
            {
                if (taskId != null)
                {
                    MSLX.SDK.MSLX.Tasks?.SetSuccess(taskId, "解压完成");
                }

                string displayTarget = string.IsNullOrEmpty(destinationFolder) ? "服务器根目录" : destinationFolder;
                await onToolExecuted("extract_archive", new
                {
                    serverId = targetServer.ID,
                    archivePath,
                    destinationFolder = displayTarget,
                    success = true
                });

                return $"[SUCCESS] 成功将归档包 `{archivePath}` 解压到 `{displayTarget}`！";
            }
            else
            {
                if (taskId != null)
                {
                    MSLX.SDK.MSLX.Tasks?.SetFailed(taskId, extractError ?? "解压失败");
                }
                return $"[ERROR] 解压归档文件失败: {extractError ?? "未知解压错误"}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleExtractArchiveAsync 异常: {Message}", ex.Message);
            return $"[ERROR] 解压归档异常: {ex.Message}";
        }
    }

    #endregion

    #region 8. compress_files

    private async Task<string> HandleCompressFilesAsync(JsonObject? args, Func<string, object, Task> onToolExecuted, string userId, bool assumeConfirmed)
    {
        string? temporaryFile = null;
        string? taskId = null;
        try
        {
            var (targetServer, safeBaseDir, err) = ResolveServerAndBaseDir(args);
            if (err != null || targetServer == null)
            {
                return err ?? "[ERROR] 未能解析服务器实例。";
            }

            string outputArchiveName = args?["output_archive_name"]?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(outputArchiveName))
            {
                return "[ERROR] 输出压缩文件名 output_archive_name 不能为空。";
            }

            string fullOutputPath = Path.GetFullPath(Path.Combine(safeBaseDir, outputArchiveName));
            if (!IsWithinServerDirectory(safeBaseDir, fullOutputPath))
            {
                return "[ERROR] 越权操作：输出文件路径超出服务器根路径。";
            }

            var sourcePaths = new List<string>();
            if (args != null && args.ContainsKey("source_paths") && args["source_paths"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item != null && !string.IsNullOrWhiteSpace(item.ToString()))
                    {
                        sourcePaths.Add(item.ToString().Trim());
                    }
                }
            }

            if (sourcePaths.Count == 0)
            {
                return "[ERROR] 请提供 source_paths 待压缩的文件或目录路径列表。";
            }

            // 收集所有要打包的物理文件
            var filesToCompress = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sp in sourcePaths)
            {
                string fullSp = Path.GetFullPath(Path.Combine(safeBaseDir, sp));
                if (!IsWithinServerDirectory(safeBaseDir, fullSp))
                {
                    continue;
                }

                if (Directory.Exists(fullSp))
                {
                    foreach (var f in Directory.GetFiles(fullSp, "*", SearchOption.AllDirectories))
                    {
                        string entryKey = Path.GetRelativePath(safeBaseDir, f).Replace('\\', '/');
                        filesToCompress[entryKey] = f;
                    }
                }
                else if (File.Exists(fullSp))
                {
                    string entryKey = Path.GetRelativePath(safeBaseDir, fullSp).Replace('\\', '/');
                    filesToCompress[entryKey] = fullSp;
                }
            }

            if (filesToCompress.Count == 0)
            {
                return $"[ERROR] 在指定的路径 [{string.Join(", ", sourcePaths)}] 中未找到任何可压缩的文件。";
            }

            string? targetOutputDir = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrEmpty(targetOutputDir) && !Directory.Exists(targetOutputDir))
            {
                Directory.CreateDirectory(targetOutputDir);
            }

            if (!assumeConfirmed && File.Exists(fullOutputPath))
                return await RequestReplacementAsync("compress_files", args?.ToJsonString() ?? "{}",
                    targetServer.ID, Path.GetRelativePath(safeBaseDir, fullOutputPath), userId, onToolExecuted);
            temporaryFile = Path.Combine(targetOutputDir!, $".mslx-compress-{Guid.NewGuid():N}-{Path.GetFileName(fullOutputPath)}");

            // 注册全局后台任务
            try
            {
                if (MSLX.SDK.MSLX.Tasks != null)
                {
                    var (taskItem, _) = MSLX.SDK.MSLX.Tasks.CreateTask(
                        string.IsNullOrEmpty(userId) ? "ai-agent" : userId,
                        (uint)targetServer.ID,
                        TaskType.Compress,
                        $"压缩打包: {Path.GetFileName(fullOutputPath)}",
                        Path.GetFileName(fullOutputPath));
                    taskId = taskItem.Id;
                    MSLX.SDK.MSLX.Tasks.UpdateProgress(taskId, 10, "正在压缩文件...", TaskState.Running);
                }
            }
            catch { }

            bool compressed = false;
            string? compressError = null;

            // 尝试通过宿主 ArchiveService 进行压缩 (若可用)
            try
            {
                var archiveServiceType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "MSLX.Daemon.Services.ArchiveService");

                if (archiveServiceType != null)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var archiveService = scope.ServiceProvider.GetService(archiveServiceType)
                        ?? _serviceProvider.GetService(archiveServiceType);

                    if (archiveService != null)
                    {
                        var method = archiveServiceType.GetMethod("CompressAsync", new[]
                        {
                            typeof(Dictionary<string, string>), typeof(string), typeof(string),
                            typeof(CancellationToken), typeof(Action<int, string>)
                        });

                        if (method != null)
                        {
                            Action<int, string> onProgress = (prog, msg) =>
                            {
                                if (taskId != null) MSLX.SDK.MSLX.Tasks?.UpdateProgress(taskId, prog, msg);
                            };

                            var taskResult = method.Invoke(archiveService, new object?[]
                            {
                                filesToCompress, temporaryFile, null, CancellationToken.None, onProgress
                            });

                            if (taskResult is Task t)
                            {
                                await t;
                                compressed = true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "调用宿主 ArchiveService 压缩失败，降级使用 BCL ZipArchive: {Message}", ex.Message);
            }

            // 降级使用标准库 BCL ZipArchive
            if (!compressed)
            {
                try
                {
                    if (File.Exists(temporaryFile)) File.Delete(temporaryFile);

                    await using (var zipFs = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var zipArchive = new ZipArchive(zipFs, ZipArchiveMode.Create))
                    {
                        int current = 0;
                        int total = filesToCompress.Count;

                        foreach (var kvp in filesToCompress)
                        {
                            var entry = zipArchive.CreateEntry(kvp.Key, CompressionLevel.Optimal);
                            await using (var entryStream = entry.Open())
                            await using (var srcStream = File.OpenRead(kvp.Value))
                            {
                                await srcStream.CopyToAsync(entryStream);
                            }

                            current++;
                            if (taskId != null && total > 0)
                            {
                                int pct = (int)((current * 100) / total);
                                MSLX.SDK.MSLX.Tasks?.UpdateProgress(taskId, pct, $"正在压缩... {current}/{total}");
                            }
                        }
                    }
                    compressed = true;
                }
                catch (Exception ex)
                {
                    compressError = ex.Message;
                }
            }

            if (compressed)
            {
                File.Move(temporaryFile, fullOutputPath, overwrite: assumeConfirmed);
                if (taskId != null)
                {
                    MSLX.SDK.MSLX.Tasks?.SetSuccess(taskId, "压缩打包完成");
                }

                long archiveSize = File.Exists(fullOutputPath) ? new FileInfo(fullOutputPath).Length : 0;
                string displayRelPath = Path.GetRelativePath(safeBaseDir, fullOutputPath);

                await onToolExecuted("compress_files", new
                {
                    serverId = targetServer.ID,
                    outputFile = displayRelPath,
                    totalFiles = filesToCompress.Count,
                    size = FormatBytes(archiveSize),
                    success = true
                });

                return $"[SUCCESS] 成功将 {filesToCompress.Count} 个文件压缩打包至 `{displayRelPath}` ({FormatBytes(archiveSize)})！";
            }
            else
            {
                if (taskId != null)
                {
                    MSLX.SDK.MSLX.Tasks?.SetFailed(taskId, compressError ?? "压缩失败");
                }
                return $"[ERROR] 打包压缩失败: {compressError ?? "未知异常"}";
            }
        }
        catch (Exception ex)
        {
            if (taskId != null) MSLX.SDK.MSLX.Tasks?.SetFailed(taskId, "压缩结果无法写入目标位置。");
            _logger.LogError(ex, "压缩文件失败");
            return $"[ERROR] 压缩文件异常: {ex.Message}";
        }
        finally
        {
            if (temporaryFile != null) File.Delete(temporaryFile);
        }
    }

    #endregion

    #region Common Helpers for Resources

    private (McServerInfo.ServerInfo? server, string safeDir, string? error) ResolveServerAndBaseDir(JsonObject? args)
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
            return (null, string.Empty, "[ERROR] 未找到目标服务器实例。");
        }

        string serverDir = targetServer.Base;
        if (string.IsNullOrEmpty(serverDir) || !Directory.Exists(serverDir))
        {
            serverDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Servers", targetServer.ID.ToString());
        }

        string safeBaseDir = Path.GetFullPath(serverDir);
        return (targetServer, safeBaseDir, null);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    #endregion
}
