using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MSLX.Plugin.AiAgents.Utils;

namespace MSLX.Plugin.AiAgents.Services;

public partial class AiService
{
    private static readonly HashSet<string> AllowedPanelImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };

    private void RegisterUploadTools(JsonArray tools)
    {
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "stage_upload_file",
                ["description"] = "将文件或文本暂存到 MSLX 上传区并返回 upload_id (即 file_key)，供后续创建服务器 (core_file_key / package_file_key)、更新核心、上传实例文件、上传面板背景图或安装本地插件使用。支持传入 UTF-8 文本 (content_text)、Base64 二进制 (content_base64) 或宿主机本地绝对路径 (local_path)；大文件也可直接通过 HTTP POST /api/plugin/mslx-plugin-ai-agents/mcp/uploads 上传。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["file_name"] = new JsonObject { ["type"] = "string", ["description"] = "文件名，如 'paper-1.21.4.jar'、'modpack.zip'、'server-icon.png'、'my-plugin.dll'" },
                        ["content_text"] = new JsonObject { ["type"] = "string", ["description"] = "UTF-8 文本内容（与 content_base64、local_path 三选一）" },
                        ["content_base64"] = new JsonObject { ["type"] = "string", ["description"] = "Base64 编码的二进制文件内容（与 content_text、local_path 三选一）" },
                        ["local_path"] = new JsonObject { ["type"] = "string", ["description"] = "宿主机本地文件绝对路径（与 content_text、content_base64 三选一）" }
                    },
                    ["required"] = new JsonArray { "file_name" }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "inspect_uploaded_archive",
                ["description"] = "检查已上传的整合包/压缩包 (.zip 或 .mrpack) 内部结构，识别是否为 Modrinth .mrpack 以及提取候选服务端核心 .jar 文件列表，便于调用 create_mc_server 时准确指定 core_filename。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["upload_id"] = new JsonObject { ["type"] = "string", ["description"] = "已暂存上传文件的 upload_id / package_file_key" },
                        ["local_path"] = new JsonObject { ["type"] = "string", ["description"] = "可选：直接检查宿主机本地 .zip / .mrpack 绝对路径" }
                    }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "upload_server_file",
                ["description"] = "上传任意二进制或文本文件到指定 Minecraft 服务器实例目录（对应面板的文件管理上传、上传 Mod/插件、上传世界/数据包、以及上传服务器根目录的 'server-icon.png' 图标等）。支持传入 upload_id (来自 /mcp/uploads 或 stage_upload_file)、content_base64、content_text 或 local_path。若目标文件已存在，将挂起等待管理员确认。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "服务器 ID，未指定时结合当前界面上下文或最新服务器" },
                        ["file_name"] = new JsonObject { ["type"] = "string", ["description"] = "目标文件名，如 'MyPlugin.jar'、'server-icon.png'、'config.yml'、'world.zip'" },
                        ["dir_path"] = new JsonObject { ["type"] = "string", ["description"] = "相对实例根目录的目标子目录，如 '' (根目录)、'plugins'、'mods'、'config'" },
                        ["upload_id"] = new JsonObject { ["type"] = "string", ["description"] = "已暂存上传的文件 Key (upload_id)" },
                        ["content_base64"] = new JsonObject { ["type"] = "string", ["description"] = "Base64 编码的二进制文件内容" },
                        ["content_text"] = new JsonObject { ["type"] = "string", ["description"] = "UTF-8 文本内容（适用于配置、脚本、文本文件）" },
                        ["local_path"] = new JsonObject { ["type"] = "string", ["description"] = "宿主机本地源文件的绝对路径" }
                    },
                    ["required"] = new JsonArray { "file_name" }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "upload_panel_image",
                ["description"] = "上传静态背景图片到 MSLX 面板公共图片库 (支持 .png, .jpg, .jpeg, .webp)，并可选直接应用为面板浅色/深色背景图。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["file_name"] = new JsonObject { ["type"] = "string", ["description"] = "图片文件名，必须以 .png、.jpg、.jpeg 或 .webp 结尾" },
                        ["upload_id"] = new JsonObject { ["type"] = "string", ["description"] = "已暂存上传的文件 Key (upload_id)" },
                        ["content_base64"] = new JsonObject { ["type"] = "string", ["description"] = "Base64 编码的图片文件内容" },
                        ["local_path"] = new JsonObject { ["type"] = "string", ["description"] = "宿主机本地图片文件的绝对路径" },
                        ["apply_to"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray { "none", "light", "dark", "both" },
                            ["description"] = "上传后是否同时设为面板背景图: 'none' (仅上传), 'light' (浅色背景), 'dark' (深色背景), 'both' (同时设置)，默认为 'none'"
                        }
                    },
                    ["required"] = new JsonArray { "file_name" }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "upload_mslx_plugin",
                ["description"] = "上传并安装本地 MSLX 面板插件 (.dll)，支持热加载启用或覆盖升级已有插件。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["file_name"] = new JsonObject { ["type"] = "string", ["description"] = "插件文件名，如 'MyPlugin.dll'（留空时从上传记录或本地路径自动获取）" },
                        ["upload_id"] = new JsonObject { ["type"] = "string", ["description"] = "已暂存上传的插件文件 Key (upload_id)" },
                        ["content_base64"] = new JsonObject { ["type"] = "string", ["description"] = "Base64 编码的插件 .dll 内容" },
                        ["local_path"] = new JsonObject { ["type"] = "string", ["description"] = "宿主机本地插件 .dll 的绝对路径" },
                        ["overwrite"] = new JsonObject { ["type"] = "boolean", ["description"] = "若插件已存在是否允许覆盖升级，默认 false" },
                        ["enabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "安装后是否启用并热加载该插件（不传则保持原状态/默认启用）" }
                    }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "update_panel_ssl",
                ["description"] = "上传或更新 MSLX 面板 HTTPS SSL 证书与私钥文本（PEM 格式），或自动生成自签名证书并切换 HTTPS 开关。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["enable_ssl"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否开启 HTTPS 加密访问" },
                        ["use_self_signed"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否自动生成本地自签名证书（默认 false）" },
                        ["certificate_text"] = new JsonObject { ["type"] = "string", ["description"] = "PEM 格式公钥证书文本 (-----BEGIN CERTIFICATE-----...)" },
                        ["private_key_text"] = new JsonObject { ["type"] = "string", ["description"] = "PEM 格式私钥文本 (-----BEGIN PRIVATE KEY-----...)" },
                        ["certificate_upload_id"] = new JsonObject { ["type"] = "string", ["description"] = "可选：通过上传接口暂存的公钥证书文件 upload_id" },
                        ["private_key_upload_id"] = new JsonObject { ["type"] = "string", ["description"] = "可选：通过上传接口暂存的私钥文件 upload_id" }
                    },
                    ["required"] = new JsonArray { "enable_ssl" }
                }
            }
        });
    }

    private async Task<string?> HandleUploadToolAsync(
        string name,
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        bool assumeConfirmed,
        string userId,
        bool isApprovalReplay = false)
    {
        switch (name)
        {
            case "stage_upload_file":
                return await HandleStageUploadFileAsync(args, onToolExecuted, userId);

            case "inspect_uploaded_archive":
                return await HandleInspectUploadedArchiveAsync(args, onToolExecuted, userId);

            case "upload_server_file":
                return await HandleUploadServerFileAsync(args, onToolExecuted, assumeConfirmed, userId, isApprovalReplay);

            case "upload_panel_image":
                return await HandleUploadPanelImageAsync(args, onToolExecuted, userId);

            case "upload_mslx_plugin":
                return await HandleUploadMslxPluginAsync(args, onToolExecuted, userId);

            case "update_panel_ssl":
                return await HandleUpdatePanelSslAsync(args, onToolExecuted, userId);

            default:
                return null;
        }
    }

    private async Task<(bool Ok, string? UploadId, string? FileName, string? TextContent, bool CleanupAfterUse, string? Error)>
        ResolveUploadSourceAsync(
            JsonObject? args,
            string ownerId,
            bool allowTextContent,
            bool trustExistingUploadOwnership,
            string fallbackFileName,
            string uploadIdField = "upload_id",
            string base64Field = "content_base64",
            string textField = "content_text",
            string localPathField = "local_path")
    {
        string? uploadId = args?[uploadIdField]?.ToString()?.Trim();
        string? base64 = args?[base64Field]?.ToString();
        string? text = allowTextContent ? args?[textField]?.ToString() : null;
        string? localPath = args?[localPathField]?.ToString()?.Trim();

        int providedCount = (!string.IsNullOrWhiteSpace(uploadId) ? 1 : 0)
            + (!string.IsNullOrEmpty(base64) ? 1 : 0)
            + (text != null ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(localPath) ? 1 : 0);

        if (providedCount == 0)
        {
            string supported = allowTextContent
                ? $"{uploadIdField}、{base64Field}、{textField} 或 {localPathField}"
                : $"{uploadIdField}、{base64Field} 或 {localPathField}";
            return (false, null, null, null, false, $"[ERROR] 请提供文件来源参数（{supported} 之一）。");
        }

        if (providedCount > 1)
        {
            return (false, null, null, null, false, "[ERROR] 只能指定一种文件来源，请勿同时提供多个来源参数。");
        }

        if (!string.IsNullOrWhiteSpace(uploadId))
        {
            if (!McpUploadService.IsValidUploadId(uploadId))
            {
                return (false, null, null, null, false, "[ERROR] upload_id 格式无效，应为 32 位十六进制字符。");
            }

            bool owned = trustExistingUploadOwnership
                ? _uploadService.StagedFileExists(uploadId)
                : _uploadService.IsOwned(uploadId, ownerId);

            if (!owned || !_uploadService.StagedFileExists(uploadId))
            {
                return (false, null, null, null, false, "[ERROR] 上传会话不存在、已过期或无权访问该文件。");
            }

            _uploadService.TryGetRecord(uploadId, out var record);
            return (true, uploadId, record?.FileName ?? fallbackFileName, null, false, null);
        }

        if (text != null)
        {
            return (true, null, fallbackFileName, text, false, null);
        }

        if (!string.IsNullOrEmpty(base64))
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64.Trim());
            }
            catch (FormatException)
            {
                return (false, null, null, null, false, "[ERROR] content_base64 不是有效的 Base64 字符串。");
            }

            if (bytes.Length == 0)
            {
                return (false, null, null, null, false, "[ERROR] 上传文件内容不能为空。");
            }

            var staged = await _uploadService.StageBytesAsync(bytes, fallbackFileName, ownerId);
            return (true, staged.UploadId, staged.FileName, null, true, null);
        }

        if (!File.Exists(localPath))
        {
            return (false, null, null, null, false, $"[ERROR] 本地文件不存在: {localPath}");
        }

        var localStaged = await _uploadService.StageLocalFileAsync(localPath!, fallbackFileName, ownerId);
        return (true, localStaged.UploadId, localStaged.FileName, null, true, null);
    }

    private async Task<string> HandleStageUploadFileAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        string userId)
    {
        string rawFileName = args?["file_name"]?.ToString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return "[ERROR] file_name 不能为空。";
        }

        string safeName = McpUploadService.SanitizeFileName(rawFileName, "upload.bin");
        string? text = args?["content_text"]?.ToString();
        string? base64 = args?["content_base64"]?.ToString();
        string? localPath = args?["local_path"]?.ToString()?.Trim();

        int providedCount = (text != null ? 1 : 0)
            + (!string.IsNullOrEmpty(base64) ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(localPath) ? 1 : 0);

        if (providedCount != 1)
        {
            return "[ERROR] 请且仅提供一种数据源：content_text、content_base64 或 local_path。";
        }

        StagedUploadRecord record;
        if (text != null)
        {
            if (text.Length == 0) return "[ERROR] content_text 不能为空。";
            record = await _uploadService.StageTextAsync(text, safeName, userId);
        }
        else if (!string.IsNullOrEmpty(base64))
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64.Trim());
            }
            catch (FormatException)
            {
                return "[ERROR] content_base64 不是有效的 Base64 字符串。";
            }
            record = await _uploadService.StageBytesAsync(bytes, safeName, userId);
        }
        else
        {
            if (!File.Exists(localPath))
            {
                return $"[ERROR] 本地文件不存在: {localPath}";
            }
            record = await _uploadService.StageLocalFileAsync(localPath!, safeName, userId);
        }

        var result = new
        {
            uploadId = record.UploadId,
            fileKey = record.UploadId,
            fileName = record.FileName,
            sizeBytes = record.SizeBytes,
            sha256 = record.Sha256
        };
        await onToolExecuted("stage_upload_file", result);
        return $"[SUCCESS] 文件 `{record.FileName}` 已暂存成功！\n" + JsonSerializer.Serialize(result);
    }

    private async Task<string> HandleInspectUploadedArchiveAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        string userId)
    {
        string? uploadId = args?["upload_id"]?.ToString()?.Trim();
        string? localPath = args?["local_path"]?.ToString()?.Trim();

        string archivePath;
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            if (!File.Exists(localPath))
            {
                return $"[ERROR] 本地压缩包不存在: {localPath}";
            }
            archivePath = localPath;
        }
        else if (!string.IsNullOrWhiteSpace(uploadId))
        {
            if (!McpUploadService.IsValidUploadId(uploadId))
            {
                return "[ERROR] upload_id 格式无效，应为 32 位十六进制字符。";
            }
            if (!_uploadService.IsOwned(uploadId, userId) || !_uploadService.StagedFileExists(uploadId))
            {
                return "[ERROR] 上传会话不存在、已过期或无权访问。";
            }
            archivePath = McpUploadService.GetStagedFilePath(uploadId);
        }
        else
        {
            return "[ERROR] 请提供 upload_id 或 local_path。";
        }

        try
        {
            var result = _uploadService.InspectArchive(archivePath);
            await onToolExecuted("inspect_uploaded_archive", result);
            return $"[SUCCESS] 压缩包解析完成：\n" + JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (InvalidDataException)
        {
            return "[ERROR] 无法解析压缩包，请确认文件是有效的 .zip 或 .mrpack 压缩包。";
        }
    }

    private async Task<string> HandleUploadServerFileAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        bool assumeConfirmed,
        string userId,
        bool isApprovalReplay = false)
    {
        var (targetServer, error) = ResolveTargetServer(args);
        if (targetServer == null) return error ?? "[ERROR] 未找到目标服务器实例。";

        string rawFileName = args?["file_name"]?.ToString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return "[ERROR] file_name 不能为空。";
        }

        string dirPath = (args?["dir_path"]?.ToString() ?? "").Trim().TrimStart('/', '\\');
        string safeFileName = rawFileName.TrimStart('/', '\\');
        string relPath = string.IsNullOrEmpty(dirPath)
            ? safeFileName
            : Path.Combine(dirPath, safeFileName);

        string safeBaseDir = GetServerDirectory(targetServer);
        Directory.CreateDirectory(safeBaseDir);
        string targetFilePath = Path.GetFullPath(Path.Combine(safeBaseDir, relPath));

        if (!IsWithinServerDirectory(safeBaseDir, targetFilePath) ||
            Path.GetRelativePath(safeBaseDir, targetFilePath) == ".")
        {
            return "[ERROR] 越权操作：不允许将文件上传到服务器实例目录之外。";
        }

        var (ok, uploadId, _, textContent, cleanupOnFailure, sourceErr) = await ResolveUploadSourceAsync(
            args,
            userId,
            allowTextContent: true,
            trustExistingUploadOwnership: isApprovalReplay,
            fallbackFileName: Path.GetFileName(targetFilePath));

        if (!ok)
        {
            return sourceErr ?? "[ERROR] 无效的文件来源。";
        }

        try
        {
            bool exists = File.Exists(targetFilePath);
            if (exists && !assumeConfirmed)
            {
                // Stage inline content before queueing approval so operations.json stays compact.
                string approvalUploadId;
                if (uploadId != null)
                {
                    approvalUploadId = uploadId;
                }
                else
                {
                    var staged = await _uploadService.StageBytesAsync(
                        Encoding.UTF8.GetBytes(textContent ?? string.Empty),
                        Path.GetFileName(targetFilePath),
                        userId);
                    approvalUploadId = staged.UploadId;
                }

                var approvalArgs = new JsonObject
                {
                    ["server_id"] = targetServer.ID,
                    ["file_name"] = safeFileName,
                    ["dir_path"] = dirPath,
                    ["upload_id"] = approvalUploadId
                };

                string displayRelPath = Path.GetRelativePath(safeBaseDir, targetFilePath);
                return await RequestReplacementAsync("upload_server_file", approvalArgs.ToJsonString(),
                    targetServer.ID, displayRelPath, userId, onToolExecuted);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);

            long finalSize;
            if (uploadId != null)
            {
                var stagedPath = McpUploadService.GetStagedFilePath(uploadId);
                File.Move(stagedPath, targetFilePath, overwrite: assumeConfirmed);
                _uploadService.RemoveSession(uploadId);
                finalSize = new FileInfo(targetFilePath).Length;
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(textContent ?? string.Empty);
                await File.WriteAllBytesAsync(targetFilePath, bytes);
                finalSize = bytes.Length;
            }

            string relativeSaved = Path.GetRelativePath(safeBaseDir, targetFilePath).Replace('\\', '/');
            var result = new
            {
                serverId = targetServer.ID,
                relativePath = relativeSaved,
                sizeBytes = finalSize,
                size = FormatBytes(finalSize),
                confirmed = exists
            };
            await onToolExecuted("upload_server_file", result);
            return $"[SUCCESS] 已成功上传文件到服务器 #{targetServer.ID} ({targetServer.Name}) 的 `{relativeSaved}` ({FormatBytes(finalSize)})！";
        }
        catch
        {
            if (cleanupOnFailure && uploadId != null)
            {
                _uploadService.DeleteStagedUpload(uploadId, userId);
            }
            throw;
        }
    }

    private async Task<string> HandleUploadPanelImageAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        string userId)
    {
        string rawFileName = args?["file_name"]?.ToString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return "[ERROR] file_name 不能为空。";
        }

        string safeFileName = McpUploadService.SanitizeFileName(rawFileName, "");
        string ext = Path.GetExtension(safeFileName);
        if (string.IsNullOrEmpty(safeFileName) || !AllowedPanelImageExtensions.Contains(ext))
        {
            return "[ERROR] 不支持的图片类型，仅支持 .png、.jpg、.jpeg、.webp。";
        }

        string applyTo = (args?["apply_to"]?.ToString()?.Trim() ?? "none").ToLowerInvariant();
        if (applyTo is not ("none" or "light" or "dark" or "both"))
        {
            return "[ERROR] apply_to 仅支持 'none'、'light'、'dark' 或 'both'。";
        }

        var (ok, uploadId, _, _, cleanupOnFailure, sourceErr) = await ResolveUploadSourceAsync(
            args,
            userId,
            allowTextContent: false,
            trustExistingUploadOwnership: false,
            fallbackFileName: safeFileName);

        if (!ok || uploadId == null)
        {
            return sourceErr ?? "[ERROR] 无效的图片来源。";
        }

        try
        {
            string imagesDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Public", "Images");
            Directory.CreateDirectory(imagesDir);
            string destPath = Path.Combine(imagesDir, safeFileName);
            string stagedPath = McpUploadService.GetStagedFilePath(uploadId);

            File.Move(stagedPath, destPath, overwrite: true);
            _uploadService.RemoveSession(uploadId);

            if (applyTo is "light" or "both")
            {
                MSLX.SDK.MSLX.Config.Main.WriteConfigKey("webPanelStyleLightBackground", safeFileName);
            }
            if (applyTo is "dark" or "both")
            {
                MSLX.SDK.MSLX.Config.Main.WriteConfigKey("webPanelStyleDarkBackground", safeFileName);
            }

            long sizeBytes = new FileInfo(destPath).Length;
            var result = new
            {
                fileName = safeFileName,
                url = $"/api/static/images/{safeFileName}",
                sizeBytes,
                size = FormatBytes(sizeBytes),
                applyTo
            };
            await onToolExecuted("upload_panel_image", result);
            return $"[SUCCESS] 面板静态图片 `{safeFileName}` 上传成功（访问路径: `/api/static/images/{safeFileName}`" +
                   (applyTo != "none" ? $"，已应用至 `{applyTo}` 背景" : "") + "）！";
        }
        catch
        {
            if (cleanupOnFailure)
            {
                _uploadService.DeleteStagedUpload(uploadId, userId);
            }
            throw;
        }
    }

    private async Task<string> HandleUploadMslxPluginAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        string userId)
    {
        string? rawFileName = args?["file_name"]?.ToString()?.Trim();
        bool overwrite = args?["overwrite"]?.GetValue<bool>() ?? false;
        bool? enabled = args != null && args.ContainsKey("enabled") && args["enabled"] != null
            ? args["enabled"]!.GetValue<bool>()
            : null;

        var (ok, uploadId, resolvedName, _, cleanupOnFailure, sourceErr) = await ResolveUploadSourceAsync(
            args,
            userId,
            allowTextContent: false,
            trustExistingUploadOwnership: false,
            fallbackFileName: string.IsNullOrWhiteSpace(rawFileName) ? "plugin.dll" : rawFileName);

        if (!ok || uploadId == null)
        {
            return sourceErr ?? "[ERROR] 无效的插件来源。";
        }

        string effectiveFileName = string.IsNullOrWhiteSpace(rawFileName)
            ? (resolvedName ?? "plugin.dll")
            : McpUploadService.SanitizeFileName(rawFileName, "plugin.dll");

        string stagedPath = McpUploadService.GetStagedFilePath(uploadId);
        try
        {
            var manifest = await _uploadService.InstallPluginAsync(
                stagedPath, effectiveFileName, overwrite, enabled, CancellationToken.None);

            await onToolExecuted("upload_mslx_plugin", manifest);
            return $"[SUCCESS] MSLX 插件安装完成：\n" + JsonSerializer.Serialize(manifest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "通过 MCP/AI 工具安装本地插件失败");
            return $"[ERROR] 插件安装失败: {ex.Message}";
        }
        finally
        {
            if (File.Exists(stagedPath))
            {
                try { File.Delete(stagedPath); } catch { }
            }
            _uploadService.RemoveSession(uploadId);
        }
    }

    private async Task<string> HandleUpdatePanelSslAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        string userId)
    {
        if (args == null || !args.ContainsKey("enable_ssl") || args["enable_ssl"] == null)
        {
            return "[ERROR] 缺少必填参数 enable_ssl。";
        }

        bool enableSsl = args["enable_ssl"]!.GetValue<bool>();
        bool useSelfSigned = args["use_self_signed"]?.GetValue<bool>() ?? false;

        string? certText = args["certificate_text"]?.ToString();
        string? keyText = args["private_key_text"]?.ToString();
        string? certUploadId = args["certificate_upload_id"]?.ToString()?.Trim();
        string? keyUploadId = args["private_key_upload_id"]?.ToString()?.Trim();

        if (!string.IsNullOrWhiteSpace(certUploadId))
        {
            if (!McpUploadService.IsValidUploadId(certUploadId) || !_uploadService.IsOwned(certUploadId, userId) || !_uploadService.StagedFileExists(certUploadId))
                return "[ERROR] 公钥证书 upload_id 无效、已过期或无权访问。";
            certText = await File.ReadAllTextAsync(McpUploadService.GetStagedFilePath(certUploadId), Encoding.UTF8);
            _uploadService.DeleteStagedUpload(certUploadId, userId);
        }

        if (!string.IsNullOrWhiteSpace(keyUploadId))
        {
            if (!McpUploadService.IsValidUploadId(keyUploadId) || !_uploadService.IsOwned(keyUploadId, userId) || !_uploadService.StagedFileExists(keyUploadId))
                return "[ERROR] 私钥 upload_id 无效、已过期或无权访问。";
            keyText = await File.ReadAllTextAsync(McpUploadService.GetStagedFilePath(keyUploadId), Encoding.UTF8);
            _uploadService.DeleteStagedUpload(keyUploadId, userId);
        }

        var (success, message) = _uploadService.UpdatePanelSslSettings(enableSsl, useSelfSigned, certText, keyText);
        await onToolExecuted("update_panel_ssl", new { enableSsl, useSelfSigned, success, message });
        return success ? $"[SUCCESS] {message}" : $"[ERROR] {message}";
    }
}
