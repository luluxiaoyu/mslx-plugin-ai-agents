using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MSLX.SDK.Models.Plugins;
using MSLX.SDK.Models.Settings;

namespace MSLX.Plugin.AiAgents.Services;

public sealed record StagedUploadRecord(
    string UploadId,
    string OwnerId,
    string FileName,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CreatedAt);

public sealed record ArchiveInspectionResult(
    bool IsMrpack,
    object? MrpackInfo,
    string BasePrefix,
    List<string> CandidateCores,
    int TotalEntries);

public sealed class McpUploadService
{
    private static readonly TimeSpan UploadLifetime = TimeSpan.FromHours(2);
    private static readonly Regex UploadIdRegex = new("^[a-fA-F0-9]{32}$", RegexOptions.Compiled);
    private static readonly object SslLock = new();

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<McpUploadService> _logger;
    private readonly ConcurrentDictionary<string, StagedUploadRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public McpUploadService(IServiceProvider serviceProvider, ILogger<McpUploadService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string UploadTempDirectory
    {
        get
        {
            var dir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppDataPath(), "Temp", "Uploads");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static bool IsValidUploadId(string? uploadId) =>
        !string.IsNullOrWhiteSpace(uploadId) && UploadIdRegex.IsMatch(uploadId);

    public static string GetStagedFilePath(string uploadId) =>
        Path.Combine(UploadTempDirectory, uploadId.ToLowerInvariant() + ".tmp");

    public async Task<StagedUploadRecord> StageStreamAsync(
        Stream input,
        string? fileName,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        CleanupExpiredRecords();

        var uploadId = Guid.NewGuid().ToString("N");
        var safeFileName = SanitizeFileName(fileName, "upload.bin");
        var finalPath = GetStagedFilePath(uploadId);
        var tempPath = finalPath + ".uploading";

        long totalBytes = 0;
        string sha256;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hasher.AppendData(buffer, 0, read);
                    totalBytes += read;
                }
            }

            if (totalBytes <= 0)
            {
                throw new InvalidOperationException("上传文件内容不能为空。");
            }

            sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }

        var record = new StagedUploadRecord(uploadId, ownerId, safeFileName, totalBytes, sha256, DateTimeOffset.UtcNow);
        _records[uploadId] = record;
        RegisterHostSession(uploadId, ownerId);
        return record;
    }

    public async Task<StagedUploadRecord> StageBytesAsync(
        byte[] bytes,
        string? fileName,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        if (bytes.Length == 0) throw new InvalidOperationException("上传文件内容不能为空。");
        await using var ms = new MemoryStream(bytes, writable: false);
        return await StageStreamAsync(ms, fileName, ownerId, cancellationToken);
    }

    public async Task<StagedUploadRecord> StageTextAsync(
        string text,
        string? fileName,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length == 0) throw new InvalidOperationException("上传文本内容不能为空。");
        await using var ms = new MemoryStream(bytes, writable: false);
        return await StageStreamAsync(ms, fileName, ownerId, cancellationToken);
    }

    public async Task<StagedUploadRecord> StageLocalFileAsync(
        string localPath,
        string? fileName,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            throw new FileNotFoundException($"本地文件不存在: {localPath}");
        }

        var resolvedName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(localPath) : fileName;
        await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await StageStreamAsync(fs, resolvedName, ownerId, cancellationToken);
    }

    public bool TryGetRecord(string? uploadId, out StagedUploadRecord? record)
    {
        record = null;
        if (!IsValidUploadId(uploadId)) return false;
        CleanupExpiredRecords();
        return _records.TryGetValue(uploadId!, out record);
    }

    public bool IsOwned(string? uploadId, string? ownerId)
    {
        if (!IsValidUploadId(uploadId) || string.IsNullOrWhiteSpace(ownerId)) return false;
        CleanupExpiredRecords();

        if (_records.TryGetValue(uploadId!, out var record))
        {
            return string.Equals(record.OwnerId, ownerId, StringComparison.Ordinal);
        }

        return CheckHostSessionOwned(uploadId!, ownerId);
    }

    public bool StagedFileExists(string? uploadId) =>
        IsValidUploadId(uploadId) && File.Exists(GetStagedFilePath(uploadId!));

    public void RemoveSession(string? uploadId)
    {
        if (!IsValidUploadId(uploadId)) return;
        _records.TryRemove(uploadId!, out _);
        RemoveHostSession(uploadId!);
    }

    public bool DeleteStagedUpload(string? uploadId, string ownerId)
    {
        if (!IsOwned(uploadId, ownerId)) return false;
        var path = GetStagedFilePath(uploadId!);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        RemoveSession(uploadId);
        return true;
    }

    public static string SanitizeFileName(string? fileName, string fallback)
    {
        var baseName = Path.GetFileName((fileName ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(baseName) || baseName is "." or ".." || baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return fallback;
        }
        return baseName;
    }

    public ArchiveInspectionResult InspectArchive(string archivePath)
    {
        using (var probeArchive = ZipFile.Open(archivePath, ZipArchiveMode.Read, Encoding.UTF8))
        {
            var mrpackEntry = probeArchive.GetEntry("modrinth.index.json");
            if (mrpackEntry != null)
            {
                using var stream = mrpackEntry.Open();
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                string? name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? versionId = root.TryGetProperty("versionId", out var v) ? v.GetString() : null;
                int filesCount = root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array
                    ? files.GetArrayLength()
                    : 0;

                string? mcVersion = null;
                string? loader = null;
                if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
                {
                    if (deps.TryGetProperty("minecraft", out var mc)) mcVersion = mc.GetString();
                    if (deps.TryGetProperty("fabric-loader", out _)) loader = "fabric";
                    else if (deps.TryGetProperty("neoforge", out _)) loader = "neoforge";
                    else if (deps.TryGetProperty("forge", out _)) loader = "forge";
                    else if (deps.TryGetProperty("quilt-loader", out _)) loader = "quilt";
                }

                return new ArchiveInspectionResult(
                    IsMrpack: true,
                    MrpackInfo: new { name, versionId, minecraftVersion = mcVersion, loader, filesCount },
                    BasePrefix: string.Empty,
                    CandidateCores: new List<string>(),
                    TotalEntries: probeArchive.Entries.Count);
            }
        }

        Encoding gbk;
        try { gbk = Encoding.GetEncoding("GBK"); }
        catch { gbk = Encoding.UTF8; }

        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Read, gbk);
        var normalizedEntries = archive.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList();

        var topSegments = normalizedEntries
            .Select(e => e.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(s => s != null)
            .Distinct()
            .ToList();

        string basePrefix = string.Empty;
        if (topSegments.Count == 1)
        {
            var potentialRoot = topSegments[0]!;
            if (normalizedEntries.Any(e => e.StartsWith(potentialRoot + "/", StringComparison.OrdinalIgnoreCase)))
            {
                basePrefix = potentialRoot + "/";
            }
        }

        var candidateCores = normalizedEntries
            .Where(n => !n.EndsWith('/') && n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .Select(n =>
            {
                if (string.IsNullOrEmpty(basePrefix)) return n;
                return n.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase)
                    ? n[basePrefix.Length..]
                    : null;
            })
            .Where(n => !string.IsNullOrEmpty(n) && !n.Contains('/'))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ArchiveInspectionResult(
            IsMrpack: false,
            MrpackInfo: null,
            BasePrefix: basePrefix,
            CandidateCores: candidateCores,
            TotalEntries: archive.Entries.Count);
    }

    public async Task<object> InstallPluginAsync(
        string tempPath,
        string? fileName,
        bool overwrite,
        bool? enabled,
        CancellationToken cancellationToken)
    {
        var packageServiceType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("MSLX.Daemon.Services.PluginsService.PluginPackageService"))
            .FirstOrDefault(t => t != null);

        var packageService = packageServiceType != null ? GetRootHostService(packageServiceType) : null;
        if (packageServiceType == null || packageService == null)
        {
            throw new InvalidOperationException("宿主插件管理服务 (PluginPackageService) 不可用。");
        }

        var installMethod = packageServiceType.GetMethod("InstallAsync", BindingFlags.Instance | BindingFlags.Public);
        if (installMethod == null)
        {
            throw new InvalidOperationException("未找到 PluginPackageService.InstallAsync 方法。");
        }

        var request = new UploadPluginRequest
        {
            FileName = string.IsNullOrWhiteSpace(fileName) ? null : SanitizeFileName(fileName, "plugin.dll"),
            Overwrite = overwrite,
            Enabled = enabled
        };

        var taskObj = installMethod.Invoke(packageService, new object?[] { tempPath, request, cancellationToken });
        if (taskObj is not Task task)
        {
            throw new InvalidOperationException("PluginPackageService.InstallAsync 未返回有效异步任务。");
        }

        await task.ConfigureAwait(false);
        var resultProp = taskObj.GetType().GetProperty("Result");
        var manifest = resultProp?.GetValue(taskObj);
        if (manifest == null)
        {
            return new { installed = true };
        }

        var manifestType = manifest.GetType();
        return new
        {
            id = manifestType.GetProperty("Id")?.GetValue(manifest)?.ToString(),
            version = manifestType.GetProperty("Version")?.GetValue(manifest)?.ToString(),
            minSdkVersion = manifestType.GetProperty("MinSdkVersion")?.GetValue(manifest)?.ToString(),
            enabled = manifestType.GetProperty("Enabled")?.GetValue(manifest),
            pending = manifestType.GetProperty("Pending")?.GetValue(manifest),
            packageHash = manifestType.GetProperty("PackageHash")?.GetValue(manifest)?.ToString()
        };
    }

    public (bool Success, string Message) UpdatePanelSslSettings(
        bool enableSsl,
        bool useSelfSigned,
        string? certificatePem,
        string? privateKeyPem)
    {
        lock (SslLock)
        {
            X509Certificate2? loadedCertificate = null;
            try
            {
                string certDir = Path.Combine(MSLX.SDK.MSLX.Config.GetAppConfigPath(), "certs");
                string pemPath = Path.Combine(certDir, "server.pem");
                string keyPath = Path.Combine(certDir, "server.key");

                string? cert = string.IsNullOrWhiteSpace(certificatePem) ? null : certificatePem.Trim();
                string? key = string.IsNullOrWhiteSpace(privateKeyPem) ? null : privateKeyPem.Trim();

                if (useSelfSigned)
                {
                    if (cert != null || key != null)
                        return (false, "使用自签名证书时，不能同时提交自定义证书或私钥内容。");

                    using var rsa = RSA.Create(2048);
                    var dnBuilder = new X500DistinguishedNameBuilder();
                    dnBuilder.AddCommonName("MSLX Local Certificate");
                    dnBuilder.AddOrganizationName("MSLX");
                    var req = new CertificateRequest(dnBuilder.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var sanBuilder = new SubjectAlternativeNameBuilder();
                    sanBuilder.AddDnsName("localhost");
                    sanBuilder.AddIpAddress(IPAddress.Loopback);
                    req.CertificateExtensions.Add(sanBuilder.Build());

                    var now = DateTimeOffset.UtcNow;
                    using var generated = req.Create(req.SubjectName, X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1),
                        now.AddDays(-1), now.AddYears(10), RandomNumberGenerator.GetBytes(16));
                    cert = generated.ExportCertificatePem();
                    key = rsa.ExportRSAPrivateKeyPem();
                }
                else
                {
                    if ((cert != null) != (key != null))
                        return (false, "自定义证书和私钥必须成对提供。");

                    if (cert != null && !CertificateHelper.IsValidCertificate(cert, out var certErr))
                        return (false, certErr ?? "证书格式无效。");

                    if (key != null && !CertificateHelper.IsValidPrivateKey(key, out var keyErr))
                        return (false, keyErr ?? "私钥格式无效。");
                }

                bool replaceCertificate = cert != null && key != null;
                if (enableSsl && !replaceCertificate && (!File.Exists(pemPath) || !File.Exists(keyPath)))
                {
                    return (false, "无法开启 SSL：请提交完整的证书和私钥，或选择自动生成自签名证书。");
                }

                if (enableSsl || (replaceCertificate && !useSelfSigned))
                {
                    string effectiveCert = replaceCertificate ? cert! : File.ReadAllText(pemPath);
                    string effectiveKey = replaceCertificate ? key! : File.ReadAllText(keyPath);
                    try
                    {
                        if (!VerifyCertAndKeyMatch(effectiveCert, effectiveKey))
                        {
                            return (false, "SSL 证书与私钥不匹配，请检查 PEM 内容。");
                        }

                        if (enableSsl)
                        {
                            using var parsed = X509Certificate2.CreateFromPem(effectiveCert, effectiveKey);
                            loadedCertificate = X509CertificateLoader.LoadPkcs12(
                                parsed.Export(X509ContentType.Pkcs12),
                                null,
                                X509KeyStorageFlags.EphemeralKeySet);
                        }
                    }
                    catch (CryptographicException ex)
                    {
                        return (false, $"无法加载 SSL 证书和私钥，请检查证书与私钥是否匹配：{ex.Message}");
                    }
                }

                if (replaceCertificate)
                {
                    Directory.CreateDirectory(certDir);
                    File.WriteAllText(pemPath, cert!);
                    File.WriteAllText(keyPath, key!);
                }

                MSLX.SDK.MSLX.Config.Main.WriteConfigKey("enableSsl", enableSsl);

                if (enableSsl && loadedCertificate != null)
                {
                    TryReplaceHostSslCertificate(loadedCertificate);
                    loadedCertificate = null;
                }

                return (true, "SSL 配置已更新（部分监听协议更改将在重启面板后生效）。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新面板 SSL 配置失败");
                return (false, $"保存 SSL 配置失败: {ex.Message}");
            }
            finally
            {
                loadedCertificate?.Dispose();
            }
        }
    }

    private static bool VerifyCertAndKeyMatch(string certPem, string keyPem)
    {
        using var cert = X509Certificate2.CreateFromPem(certPem);
        var certSpki = cert.PublicKey.ExportSubjectPublicKeyInfo();

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(keyPem);
            return certSpki.SequenceEqual(rsa.ExportSubjectPublicKeyInfo());
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            // Try ECDSA below.
        }

        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(keyPem);
            return certSpki.SequenceEqual(ecdsa.ExportSubjectPublicKeyInfo());
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    private void TryReplaceHostSslCertificate(X509Certificate2 certificate)
    {
        try
        {
            var sslMgrType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("MSLX.Daemon.Utils.SslCertificateManager"))
                .FirstOrDefault(t => t != null);
            var replaceMethod = sslMgrType?.GetMethod("ReplaceCertificate", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            replaceMethod?.Invoke(null, new object[] { certificate });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新宿主内存 SSL 证书缓存失败，重启后将生效");
        }
    }

    private void CleanupExpiredRecords()
    {
        var cutoff = DateTimeOffset.UtcNow - UploadLifetime;
        foreach (var pair in _records)
        {
            if (pair.Value.CreatedAt < cutoff)
            {
                _records.TryRemove(pair.Key, out _);
            }
        }
    }

    private object? GetRootHostService(Type serviceType)
    {
        try
        {
            var pluginManagerType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("MSLX.Daemon.Services.PluginsService.PluginManager"))
                .FirstOrDefault(t => t != null);
            if (pluginManagerType != null)
            {
                var pm = _serviceProvider.GetService(pluginManagerType);
                if (pm != null)
                {
                    var spField = pluginManagerType.GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (spField?.GetValue(pm) is IServiceProvider rootSp)
                    {
                        var resolved = rootSp.GetService(serviceType);
                        if (resolved != null) return resolved;
                    }
                }
            }
            return _serviceProvider.GetService(serviceType);
        }
        catch
        {
            return null;
        }
    }

    private (object? Service, Type? ServiceType) GetHostUploadSessionService()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("MSLX.Daemon.Services.UploadSessionService"))
            .FirstOrDefault(t => t != null);
        return type == null ? (null, null) : (GetRootHostService(type), type);
    }

    private void RegisterHostSession(string uploadId, string ownerId)
    {
        try
        {
            var (svc, type) = GetHostUploadSessionService();
            var method = type?.GetMethod("Create", new[] { typeof(string), typeof(string), typeof(string) });
            method?.Invoke(svc, new object?[] { uploadId, ownerId, "local" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "向宿主 UploadSessionService 注册上传会话失败: {UploadId}", uploadId);
        }
    }

    private bool CheckHostSessionOwned(string uploadId, string ownerId)
    {
        try
        {
            var (svc, type) = GetHostUploadSessionService();
            var method = type?.GetMethod("IsOwned", new[] { typeof(string), typeof(string) });
            if (svc != null && method != null)
            {
                return method.Invoke(svc, new object?[] { uploadId, ownerId }) is true;
            }
        }
        catch { }
        return false;
    }

    private void RemoveHostSession(string uploadId)
    {
        try
        {
            var (svc, type) = GetHostUploadSessionService();
            var method = type?.GetMethod("Remove", new[] { typeof(string) });
            method?.Invoke(svc, new object?[] { uploadId });
        }
        catch { }
    }
}
