using System.Security.Cryptography;
using System.Text.Json;
using MSLX.Plugin.AiAgents.Models;
using MSLX.SDK;
using MSLX.SDK.Interfaces;

namespace MSLX.Plugin.AiAgents.Services;

public class McpTokenManager
{
    private const int ToolCallsPerMinute = 60;
    private readonly object _lock = new();
    private readonly List<McpTokenRecord> _tokens = new();
    private string _filePath = string.Empty;
    private bool _initialized = false;

    public McpTokenManager()
    {
        InitializeStorage();
    }

    private void InitializeStorage()
    {
        lock (_lock)
        {
            if (_initialized) return;

            try
            {
                string dataDir;
                try
                {
                    dataDir = MSLXPluginEntry.Instance.Config().GetDataPath();
                }
                catch
                {
                    dataDir = Path.Combine(AppContext.BaseDirectory, "DaemonData", "PluginsData", "mslx-plugins-ai-agents");
                }

                if (!Directory.Exists(dataDir))
                {
                    Directory.CreateDirectory(dataDir);
                }

                _filePath = Path.Combine(dataDir, "mcp_tokens.json");

                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var loaded = JsonSerializer.Deserialize<List<McpTokenRecord>>(json);
                        if (loaded != null)
                        {
                            _tokens.Clear();
                            _tokens.AddRange(loaded);
                        }
                    }
                }
                else
                {
                    // 若无 token 文件，可初始化一个默认的永久本地 Token 便于首次体验
                    var defaultToken = new McpTokenRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = "Default Local Agent",
                        Token = GenerateTokenString(),
                        CreatedAt = DateTime.UtcNow,
                        ExpiresAt = null,
                        IsRevoked = false
                    };
                    _tokens.Add(defaultToken);
                    SaveToFileLocked();
                }

                _initialized = true;
            }
            catch (Exception ex)
            {
                SDK.MSLX.Logger.Error($"[McpTokenManager] 初始化 Token 存储失败: {ex.Message}");
            }
        }
    }

    private void SaveToFileLocked()
    {
        try
        {
            if (string.IsNullOrEmpty(_filePath)) return;
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_tokens, options);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Error($"[McpTokenManager] 保存 Token 异常: {ex.Message}");
        }
    }

    private static string GenerateTokenString()
    {
        byte[] randomBytes = new byte[16];
        RandomNumberGenerator.Fill(randomBytes);
        return $"mslx_mcp_{Convert.ToHexString(randomBytes).ToLowerInvariant()}";
    }

    private static string MaskToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return string.Empty;
        if (token.Length <= 14) return $"{token[..Math.Min(9, token.Length)]}...";
        return $"{token[..13]}...{token[^4..]}";
    }

    public McpTokenCreatedDto GenerateToken(string name, int? expiresInDays)
    {
        InitializeStorage();
        lock (_lock)
        {
            DateTime? expiresAt = expiresInDays.HasValue && expiresInDays.Value > 0
                ? DateTime.UtcNow.AddDays(expiresInDays.Value)
                : null;

            var record = new McpTokenRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? "MCP Agent" : name.Trim(),
                Token = GenerateTokenString(),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt,
                IsRevoked = false
            };

            _tokens.Add(record);
            SaveToFileLocked();

            return new McpTokenCreatedDto
            {
                Id = record.Id,
                Name = record.Name,
                Token = record.Token,
                MaskedToken = MaskToken(record.Token),
                CreatedAt = record.CreatedAt,
                ExpiresAt = record.ExpiresAt,
                LastUsedAt = record.LastUsedAt,
                IsRevoked = record.IsRevoked,
                Status = "active"
            };
        }
    }

    public bool ValidateToken(string? token, out McpTokenRecord? matchedRecord)
    {
        matchedRecord = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        InitializeStorage();
        var rawToken = token.Trim();
        if (rawToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            rawToken = rawToken["Bearer ".Length..].Trim();
        }

        lock (_lock)
        {
            var found = _tokens.FirstOrDefault(t => t.Token == rawToken);
            var now = DateTime.UtcNow;
            if (found == null || !IsUsable(found, now))
                return false;

            found.LastUsedAt = now;
            matchedRecord = found;

            _ = Task.Run(() =>
            {
                lock (_lock)
                {
                    SaveToFileLocked();
                }
            });

            return true;
        }
    }

    internal bool IsActive(McpTokenRecord token)
    {
        lock (_lock)
        {
            return _tokens.Contains(token) && IsUsable(token, DateTime.UtcNow);
        }
    }

    internal bool TryUseTool(McpTokenRecord token)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!_tokens.Contains(token) || !IsUsable(token, now))
                return false;

            if (now - token.ToolCallWindowStartedAt >= TimeSpan.FromMinutes(1))
            {
                token.ToolCallWindowStartedAt = now;
                token.ToolCallsInWindow = 0;
            }

            if (token.ToolCallsInWindow >= ToolCallsPerMinute)
                return false;

            token.ToolCallsInWindow++;
            return true;
        }
    }

    private static bool IsUsable(McpTokenRecord token, DateTime now) =>
        !token.IsRevoked && (!token.ExpiresAt.HasValue || now < token.ExpiresAt.Value);

    public List<McpTokenDto> ListTokens()
    {
        InitializeStorage();
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            return _tokens.Select(t =>
            {
                string status = "active";
                if (t.IsRevoked)
                {
                    status = "revoked";
                }
                else if (t.ExpiresAt.HasValue && now > t.ExpiresAt.Value)
                {
                    status = "expired";
                }

                string masked = MaskToken(t.Token);
                return new McpTokenDto
                {
                    Id = t.Id,
                    Name = t.Name,
                    Token = t.Token,
                    MaskedToken = masked,
                    CreatedAt = t.CreatedAt,
                    ExpiresAt = t.ExpiresAt,
                    LastUsedAt = t.LastUsedAt,
                    IsRevoked = t.IsRevoked,
                    Status = status
                };
            }).OrderByDescending(t => t.CreatedAt).ToList();
        }
    }

    public bool RevokeToken(string id)
    {
        InitializeStorage();
        lock (_lock)
        {
            var found = _tokens.FirstOrDefault(t => t.Id == id);
            if (found == null) return false;

            found.IsRevoked = true;
            found.RevokedAt = DateTime.UtcNow;
            SaveToFileLocked();
            return true;
        }
    }

    public bool DeleteToken(string id)
    {
        InitializeStorage();
        lock (_lock)
        {
            var found = _tokens.FirstOrDefault(t => t.Id == id);
            if (found == null) return false;

            _tokens.Remove(found);
            SaveToFileLocked();
            return true;
        }
    }
}
