using System.Security.Cryptography;
using System.Text.Json;
using MSLX.Plugin.AiAgents.Models;
using MSLX.Plugin.AiAgents.Utils;

namespace MSLX.Plugin.AiAgents.Services;

public sealed class McpTokenManager : IDisposable
{
    private readonly object _gate = new();
    private readonly List<McpTokenRecord> _tokens;
    private readonly string _filePath = Path.Combine(PluginData.DirectoryPath, "mcp_tokens.json");
    private Timer? _saveTimer;
    private bool _dirty;

    public McpTokenManager()
    {
        _tokens = File.Exists(_filePath)
            ? JsonSerializer.Deserialize<List<McpTokenRecord>>(File.ReadAllText(_filePath)) ?? new()
            : new();
        if (!File.Exists(_filePath)) GenerateToken("Default Local Agent", null);
    }

    private void Save()
    {
        PluginData.WriteJson(_filePath, _tokens);
        _dirty = false;
    }

    private void SaveUsage()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            try { Save(); }
            catch (Exception ex)
            {
                MSLX.SDK.MSLX.Logger.Error($"无法保存 MCP 令牌使用时间: {ex.Message}");
                _saveTimer?.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        _saveTimer ??= new Timer(_ => SaveUsage());
        _saveTimer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            SaveUsage();
        }
    }

    public McpTokenDto GenerateToken(string name, int? expiresInDays, bool readOnly = false, int[]? serverIds = null)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var record = new McpTokenRecord
            {
                Name = name.Trim(),
                Token = $"mslx_mcp_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}",
                CreatedAt = now,
                ExpiresAt = expiresInDays > 0 ? now.AddDays(expiresInDays.Value) : null,
                ReadOnly = readOnly,
                ServerIds = serverIds?.Distinct().ToArray()
            };
            _tokens.Add(record);
            try { Save(); }
            catch { _tokens.Remove(record); throw; }
            return ToDto(record, now);
        }
    }

    public bool ValidateToken(string? token, out McpTokenRecord? matchedRecord)
    {
        lock (_gate)
        {
            var raw = token?.Trim();
            if (raw?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true) raw = raw[7..].Trim();
            matchedRecord = _tokens.Find(record => record.Token == raw && IsUsable(record, DateTime.UtcNow));
            return matchedRecord != null;
        }
    }

    internal bool IsActive(McpTokenRecord token)
    {
        lock (_gate) return _tokens.Contains(token) && IsUsable(token, DateTime.UtcNow);
    }

    internal bool IsActive(string id)
    {
        lock (_gate) return _tokens.Any(token => token.Id == id && IsUsable(token, DateTime.UtcNow));
    }

    internal bool CanApprove(string id, int serverId)
    {
        lock (_gate)
            return _tokens.Any(token => token.Id == id && IsUsable(token, DateTime.UtcNow) &&
                !token.ReadOnly && (token.ServerIds == null || token.ServerIds.Contains(serverId)));
    }

    internal string? GetName(string id)
    {
        lock (_gate) return _tokens.Find(token => token.Id == id)?.Name;
    }

    internal bool MarkUsed(McpTokenRecord token, bool toolListRead)
    {
        lock (_gate)
        {
            if (!_tokens.Contains(token) || !IsUsable(token, DateTime.UtcNow)) return false;
            token.LastUsedAt = DateTime.UtcNow;
            if (toolListRead) token.ConnectedAt ??= token.LastUsedAt;
            MarkDirty();
            return true;
        }
    }

    internal bool TryUseTool(McpTokenRecord token)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!_tokens.Contains(token) || !IsUsable(token, now)) return false;
            if (now - token.ToolCallWindowStartedAt >= TimeSpan.FromMinutes(1))
            {
                token.ToolCallWindowStartedAt = now;
                token.ToolCallsInWindow = 0;
            }
            if (token.ToolCallsInWindow >= 60) return false;
            token.ToolCallsInWindow++;
            return true;
        }
    }

    private static bool IsUsable(McpTokenRecord token, DateTime now) =>
        !token.IsRevoked && (token.ExpiresAt == null || now < token.ExpiresAt);

    private static McpTokenDto ToDto(McpTokenRecord token, DateTime now) => new()
    {
        Id = token.Id,
        Name = token.Name,
        Token = IsUsable(token, now) ? token.Token : "",
        MaskedToken = token.Token.Length > 14 ? $"{token.Token[..13]}…{token.Token[^4..]}" : "••••",
        CreatedAt = token.CreatedAt,
        ExpiresAt = token.ExpiresAt,
        LastUsedAt = token.LastUsedAt,
        ConnectedAt = token.ConnectedAt,
        ReadOnly = token.ReadOnly,
        ServerIds = token.ServerIds,
        IsRevoked = token.IsRevoked,
        Status = token.IsRevoked ? "revoked" : IsUsable(token, now) ? "active" : "expired"
    };

    public List<McpTokenDto> ListTokens()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            return _tokens.OrderByDescending(token => token.CreatedAt).Select(token => ToDto(token, now)).ToList();
        }
    }

    public bool RevokeToken(string id)
    {
        lock (_gate)
        {
            var found = _tokens.Find(token => token.Id == id);
            if (found == null) return false;
            if (found.IsRevoked) return true;
            found.IsRevoked = true;
            found.RevokedAt = DateTime.UtcNow;
            try { Save(); }
            catch { found.IsRevoked = false; found.RevokedAt = null; throw; }
            return true;
        }
    }

    public bool DeleteToken(string id)
    {
        lock (_gate)
        {
            var found = _tokens.Find(token => token.Id == id);
            if (found == null) return false;
            if (IsUsable(found, DateTime.UtcNow))
                throw new InvalidOperationException("请先吊销该访问令牌，再永久删除。");
            _tokens.Remove(found);
            try { Save(); }
            catch { _tokens.Add(found); throw; }
            return true;
        }
    }
}
