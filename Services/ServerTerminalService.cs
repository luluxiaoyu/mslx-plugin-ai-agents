using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MSLX.Plugin.AiAgents.Utils;

namespace MSLX.Plugin.AiAgents.Services;

public sealed class ServerTerminalService
{
    public const int MaxGrantMinutes = 7 * 24 * 60; // 7 天 (10080 分钟)
    public const int DefaultTimeoutSeconds = 30;
    public const int MaxTimeoutSeconds = 300;
    private const int MaxStreamChars = 32 * 1024;

    private const string PluginId = "mslx-plugin-ai-agents";
    private const string McpAllowTerminalByDefaultKey = "mcpAllowTerminalByDefault";
    private const string ApiKeyTerminalAutoApproveKey = "aiTerminalAutoApprove";
    private const string FullPermissionModeKey = "fullPermissionMode";

    private readonly McpTokenManager _tokenManager;
    private readonly ILogger<ServerTerminalService> _logger;
    private readonly object _grantLock = new();
    private List<TerminalPermissionGrant>? _grants;

    private string GrantsPath => Path.Combine(PluginData.DirectoryPath, "terminal_grants.json");

    public ServerTerminalService(McpTokenManager tokenManager, ILogger<ServerTerminalService> logger)
    {
        _tokenManager = tokenManager;
        _logger = logger;
    }

    public static bool IsFullPermissionMode()
    {
        var config = MSLX.SDK.MSLX.Config.GetPluginConfig(PluginId).ReadConfig();
        return (bool?)config[FullPermissionModeKey] ?? false;
    }

    public static void SetFullPermissionMode(bool enabled)
    {
        MSLX.SDK.MSLX.Config.GetPluginConfig(PluginId).WriteConfigKey(FullPermissionModeKey, enabled);
    }

    public bool IsMcpTerminalEnabledByDefault()
    {
        var config = MSLX.SDK.MSLX.Config.GetPluginConfig(PluginId).ReadConfig();
        return (bool?)config[McpAllowTerminalByDefaultKey] ?? false;
    }

    public void SetMcpTerminalEnabledByDefault(bool enabled)
    {
        MSLX.SDK.MSLX.Config.GetPluginConfig(PluginId).WriteConfigKey(McpAllowTerminalByDefaultKey, enabled);
    }

    public bool IsApiKeyTerminalAutoApprove()
    {
        var config = MSLX.SDK.MSLX.Config.GetPluginConfig(PluginId).ReadConfig();
        return (bool?)config[ApiKeyTerminalAutoApproveKey] ?? false;
    }

    public void SetApiKeyTerminalAutoApprove(bool enabled)
    {
        MSLX.SDK.MSLX.Config.GetPluginConfig(PluginId).WriteConfigKey(ApiKeyTerminalAutoApproveKey, enabled);
    }

    private List<TerminalPermissionGrant> LoadGrantsLocked()
    {
        if (_grants != null) return _grants;
        if (!File.Exists(GrantsPath))
        {
            _grants = new List<TerminalPermissionGrant>();
            return _grants;
        }

        try
        {
            _grants = JsonSerializer.Deserialize<List<TerminalPermissionGrant>>(File.ReadAllText(GrantsPath)) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取终端权限授权记录失败，将重置为空列表");
            _grants = new List<TerminalPermissionGrant>();
        }
        return _grants;
    }

    private void SaveGrantsLocked() => PluginData.WriteJson(GrantsPath, _grants ?? new List<TerminalPermissionGrant>());

    private bool IsGrantCurrentlyValidLocked(TerminalPermissionGrant grant, DateTime now)
    {
        if (grant.Revoked || grant.ExpiresAt <= now)
            return false;

        if (grant.Owner.StartsWith("mcp:", StringComparison.Ordinal))
        {
            var tokenId = grant.Owner[4..];
            if (!_tokenManager.CanApprove(tokenId, grant.ServerId))
                return false;
        }

        return true;
    }

    public TerminalPermissionGrant GrantTemporaryAccess(
        string owner,
        string requesterName,
        int serverId,
        string serverName,
        int grantedMinutes,
        int requestedHours,
        int requestedMinutes,
        string? reason,
        string? approver)
    {
        var safeMinutes = Math.Clamp(grantedMinutes, 1, MaxGrantMinutes);
        var now = DateTime.UtcNow;

        lock (_grantLock)
        {
            var grants = LoadGrantsLocked();

            // 清理超过 24 小时前失效的旧记录，并将同 owner + serverId 的旧有效授权替换为本次最新窗口
            grants.RemoveAll(g => (g.Revoked || g.ExpiresAt <= now) && g.ExpiresAt <= now.AddHours(-24));
            foreach (var existing in grants.Where(g =>
                         !g.Revoked && g.Owner == owner && g.ServerId == serverId))
            {
                existing.Revoked = true;
                existing.RevokedAt = now;
            }

            var created = new TerminalPermissionGrant
            {
                Id = Guid.NewGuid().ToString("N"),
                Owner = owner,
                RequesterName = requesterName,
                ServerId = serverId,
                ServerName = serverName,
                RequestedHours = requestedHours,
                RequestedMinutes = requestedMinutes,
                GrantedMinutes = safeMinutes,
                Reason = reason,
                Approver = approver,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(safeMinutes)
            };

            grants.Add(created);
            SaveGrantsLocked();
            return created;
        }
    }

    public bool HasActiveGrant(string owner, int serverId, out TerminalPermissionGrant? activeGrant)
    {
        var now = DateTime.UtcNow;
        lock (_grantLock)
        {
            var grants = LoadGrantsLocked();
            activeGrant = grants
                .Where(g => g.Owner == owner && g.ServerId == serverId && IsGrantCurrentlyValidLocked(g, now))
                .OrderByDescending(g => g.ExpiresAt)
                .FirstOrDefault();
            return activeGrant != null;
        }
    }

    public IReadOnlyList<object> ListActiveGrants()
    {
        var now = DateTime.UtcNow;
        lock (_grantLock)
        {
            var grants = LoadGrantsLocked();
            return grants
                .Where(g => IsGrantCurrentlyValidLocked(g, now))
                .OrderByDescending(g => g.CreatedAt)
                .Select(g =>
                {
                    var requester = g.Owner.StartsWith("mcp:", StringComparison.Ordinal)
                        ? _tokenManager.GetName(g.Owner[4..]) ?? g.RequesterName
                        : g.RequesterName;
                    var remainingSeconds = Math.Max(0, (int)(g.ExpiresAt - now).TotalSeconds);
                    return (object)new
                    {
                        id = g.Id,
                        owner = g.Owner,
                        source = g.Owner.StartsWith("mcp:", StringComparison.Ordinal) ? "mcp" : "chat",
                        requesterName = requester,
                        serverId = g.ServerId,
                        serverName = g.ServerName,
                        grantedMinutes = g.GrantedMinutes,
                        durationLabel = FormatDuration(g.GrantedMinutes),
                        reason = g.Reason,
                        approver = g.Approver,
                        createdAt = g.CreatedAt,
                        expiresAt = g.ExpiresAt,
                        remainingSeconds
                    };
                })
                .ToArray();
        }
    }

    public bool RevokeGrant(string grantId)
    {
        if (string.IsNullOrWhiteSpace(grantId)) return false;
        lock (_grantLock)
        {
            var grants = LoadGrantsLocked();
            var target = grants.FirstOrDefault(g => g.Id == grantId);
            if (target == null) return false;
            if (target.Revoked) return true;
            target.Revoked = true;
            target.RevokedAt = DateTime.UtcNow;
            SaveGrantsLocked();
            return true;
        }
    }

    public static string FormatDuration(int totalMinutes)
    {
        if (totalMinutes <= 0) return "0 分钟";
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        if (hours > 0 && minutes > 0) return $"{hours} 小时 {minutes} 分钟";
        if (hours > 0) return $"{hours} 小时";
        return $"{minutes} 分钟";
    }

    public async Task<TerminalExecutionResult> ExecuteCommandAsync(
        string workingDirectory,
        string command,
        string? requestedShell,
        int timeoutSeconds,
        int serverId,
        string serverName,
        string serverBaseDir,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("命令内容不能为空。", nameof(command));
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"工作目录不存在: {workingDirectory}");

        var (fileName, arguments, shellLabel) = BuildShellInvocation(requestedShell, command);
        var effectiveTimeout = Math.Clamp(timeoutSeconds <= 0 ? DefaultTimeoutSeconds : timeoutSeconds, 1, MaxTimeoutSeconds);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        psi.Environment["MSLX_SERVER_ID"] = serverId.ToString();
        psi.Environment["MSLX_SERVER_NAME"] = serverName;
        psi.Environment["MSLX_SERVER_DIR"] = serverBaseDir;

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"无法启动终端进程 ({shellLabel})。");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException($"无法启动终端程序 `{fileName}`: {ex.Message}", ex);
        }

        // 关闭标准输入，避免等待交互输入的命令永久阻塞
        try { process.StandardInput.Close(); } catch { }

        var stdoutTask = ReadBoundedStreamAsync(process.StandardOutput, MaxStreamChars);
        var stderrTask = ReadBoundedStreamAsync(process.StandardError, MaxStreamChars);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeout));

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKillProcessTree(process);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        var (stdout, stdoutTruncated) = await stdoutTask;
        var (stderr, stderrTruncated) = await stderrTask;
        sw.Stop();

        int exitCode = timedOut ? -1 : process.ExitCode;
        return new TerminalExecutionResult(
            ExitCode: exitCode,
            TimedOut: timedOut,
            DurationMs: sw.ElapsedMilliseconds,
            ShellUsed: shellLabel,
            WorkingDirectory: workingDirectory,
            Stdout: stdout,
            Stderr: stderr,
            StdoutTruncated: stdoutTruncated,
            StderrTruncated: stderrTruncated);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static async Task<(string Content, bool Truncated)> ReadBoundedStreamAsync(StreamReader reader, int maxChars)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        bool truncated = false;

        while (true)
        {
            int read = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (read <= 0) break;

            if (sb.Length < maxChars)
            {
                int remaining = maxChars - sb.Length;
                if (read <= remaining)
                {
                    sb.Append(buffer, 0, read);
                }
                else
                {
                    sb.Append(buffer, 0, remaining);
                    truncated = true;
                }
            }
            else
            {
                truncated = true;
            }
        }

        return (sb.ToString(), truncated);
    }

    private static (string FileName, IReadOnlyList<string> Arguments, string ShellLabel) BuildShellInvocation(
        string? requestedShell,
        string command)
    {
        var normalized = (requestedShell ?? "auto").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) normalized = "auto";

        if (OperatingSystem.IsWindows())
        {
            return normalized switch
            {
                "auto" or "powershell" => BuildWindowsPowerShellInvocation(command, preferCore: false),
                "pwsh" => BuildWindowsPowerShellInvocation(command, preferCore: true),
                "cmd" => ("cmd.exe", new[] { "/d", "/s", "/c", $"chcp 65001>nul & {command}" }, "cmd.exe"),
                _ => throw new InvalidOperationException($"Windows 环境不支持终端类型 `{requestedShell}`，请使用 auto、powershell、pwsh 或 cmd。")
            };
        }

        if (normalized is "powershell" or "cmd")
            throw new InvalidOperationException($"当前为非 Windows 系统，不支持 `{requestedShell}`；请使用 auto、zsh、bash、sh{(FindExecutable("pwsh") != null ? " 或 pwsh" : "")}。");

        if (normalized == "pwsh")
        {
            var pwsh = FindExecutable("pwsh")
                ?? throw new InvalidOperationException("当前系统未安装 pwsh (PowerShell Core)。");
            return (pwsh, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command }, "pwsh");
        }

        if (normalized == "auto")
        {
            string? autoShell = OperatingSystem.IsMacOS()
                ? FirstExistingFile("/bin/zsh", "/bin/bash", "/bin/sh")
                : FirstExistingFile("/bin/bash", "/usr/bin/bash", "/bin/zsh", "/usr/bin/zsh", "/bin/sh");
            autoShell ??= "/bin/sh";
            return (autoShell, new[] { "-c", command }, Path.GetFileName(autoShell));
        }

        return normalized switch
        {
            "zsh" => ResolveUnixShell("zsh", "/bin/zsh", "/usr/bin/zsh", "/opt/homebrew/bin/zsh", command),
            "bash" => ResolveUnixShell("bash", "/bin/bash", "/usr/bin/bash", "/opt/homebrew/bin/bash", command),
            "sh" => ResolveUnixShell("sh", "/bin/sh", "/usr/bin/sh", null, command),
            _ => throw new InvalidOperationException($"不支持的终端类型 `{requestedShell}`，可选：auto、powershell、pwsh、cmd、zsh、bash、sh。")
        };
    }

    private static (string FileName, IReadOnlyList<string> Arguments, string ShellLabel) BuildWindowsPowerShellInvocation(
        string command,
        bool preferCore)
    {
        string psScript =
            "$ProgressPreference = 'SilentlyContinue'\n" +
            "[Console]::InputEncoding = [System.Text.Encoding]::UTF8\n" +
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
            "$OutputEncoding = [System.Text.Encoding]::UTF8\n" +
            command;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));

        if (preferCore)
        {
            var pwsh = FindExecutable("pwsh.exe") ?? "pwsh.exe";
            return (pwsh, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }, "pwsh");
        }

        var winPsPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(winPsPath))
        {
            return (winPsPath, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded }, "powershell");
        }

        var pathPs = FindExecutable("powershell.exe");
        if (pathPs != null)
        {
            return (pathPs, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded }, "powershell");
        }

        var fallbackPwsh = FindExecutable("pwsh.exe") ?? "powershell.exe";
        var label = fallbackPwsh.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ? "pwsh" : "powershell";
        var args = label == "pwsh"
            ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }
            : new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded };
        return (fallbackPwsh, args, label);
    }

    private static (string FileName, IReadOnlyList<string> Arguments, string ShellLabel) ResolveUnixShell(
        string name, string primary, string secondary, string? tertiary, string command)
    {
        var path = FirstExistingFile(primary, secondary, tertiary) ?? FindExecutable(name)
            ?? throw new InvalidOperationException($"当前系统未找到 `{name}`。");
        return (path, new[] { "-c", command }, name);
    }

    private static string? FirstExistingFile(params string?[] candidates) =>
        candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

    private static string? FindExecutable(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), name);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    public sealed class TerminalPermissionGrant
    {
        public required string Id { get; init; }
        public required string Owner { get; init; }
        public required string RequesterName { get; init; }
        public required int ServerId { get; init; }
        public required string ServerName { get; init; }
        public int RequestedHours { get; init; }
        public int RequestedMinutes { get; init; }
        public int GrantedMinutes { get; init; }
        public string? Reason { get; init; }
        public string? Approver { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime ExpiresAt { get; set; }
        public bool Revoked { get; set; }
        public DateTime? RevokedAt { get; set; }
    }

    public sealed record TerminalExecutionResult(
        int ExitCode,
        bool TimedOut,
        long DurationMs,
        string ShellUsed,
        string WorkingDirectory,
        string Stdout,
        string Stderr,
        bool StdoutTruncated,
        bool StderrTruncated);
}
