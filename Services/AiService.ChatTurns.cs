using System.Text.Json;
using System.Text.Json.Nodes;
using MSLX.Plugin.AiAgents.Utils;

namespace MSLX.Plugin.AiAgents.Services;

public partial class AiService
{
    private readonly object _turnGate = new();
    private readonly Dictionary<string, CancellationTokenSource> _activeTurns = new();
    private Dictionary<string, ChatTurn>? _turns;
    private string TurnPath => Path.Combine(PluginData.DirectoryPath, "chat_turns.json");

    private Dictionary<string, ChatTurn> LoadTurns()
    {
        if (_turns != null) return _turns;
        var turns = File.Exists(TurnPath) ?
            JsonSerializer.Deserialize<Dictionary<string, ChatTurn>>(File.ReadAllText(TurnPath)) ?? new() : new();
        foreach (var turn in turns.Values.Where(turn => turn.Status == "running"))
            turn.Status = "unknown";
        return _turns = turns;
    }

    public object GetTurnStatus(string userId, string turnId)
    {
        lock (_turnGate)
            return LoadTurns().TryGetValue(turnId, out var turn) && turn.Owner == userId
                ? new { status = turn.Status, tools = turn.Tools.Select(DescribeTurnTool).ToArray() }
                : new { status = "unknown", tools = Array.Empty<TurnTool>() };
    }

    public bool StopTurn(string userId, string turnId)
    {
        CancellationTokenSource? source;
        lock (_turnGate)
        {
            if (!LoadTurns().TryGetValue(turnId, out var turn) || turn.Owner != userId ||
                !_activeTurns.TryGetValue(turnId, out source)) return false;
        }
        try { source.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    internal void StopAllChatTurns()
    {
        CancellationTokenSource[] active;
        lock (_turnGate) active = _activeTurns.Values.ToArray();
        foreach (var source in active)
        {
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task RunChatTurnAsync(string userId, string turnId, JsonArray messages, Func<string, Task> onChunk,
        Func<string, object, Task> onTool, CancellationToken connectionAborted)
    {
        if (string.IsNullOrWhiteSpace(userId) || !Guid.TryParseExact(turnId, "N", out _))
            throw new ArgumentException("请求编号无效。");
        using var source = CancellationTokenSource.CreateLinkedTokenSource(connectionAborted);
        var turn = new ChatTurn { Owner = userId, CreatedAt = DateTime.UtcNow };
        lock (_turnGate)
        {
            var turns = LoadTurns();
            if (turns.ContainsKey(turnId)) return;
            foreach (var expired in turns.Where(pair => pair.Value.CreatedAt < DateTime.UtcNow.AddDays(-7) &&
                         !_activeTurns.ContainsKey(pair.Key)).Select(pair => pair.Key).ToArray())
                turns.Remove(expired);
            turns.Add(turnId, turn);
            try { PluginData.WriteJson(TurnPath, turns); }
            catch { turns.Remove(turnId); throw; }
            _activeTurns.Add(turnId, source);
        }

        string status = "unknown";
        try
        {
            await ProcessChatAsync(messages, onChunk, onTool, userId, source.Token, (name, state, taskId, operationId) =>
            {
                lock (_turnGate)
                {
                    if (state == "executing") turn.Tools.Add(new TurnTool(name, state, taskId, operationId));
                    else if (turn.Tools.Count > 0) turn.Tools[^1] = new TurnTool(name, state, taskId, operationId);
                    PluginData.WriteJson(TurnPath, _turns);
                }
            });
            status = "succeeded";
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { status = "cancelled"; }
        catch { status = "failed"; throw; }
        finally
        {
            lock (_turnGate)
            {
                _activeTurns.Remove(turnId);
                turn.Status = status;
                PluginData.WriteJson(TurnPath, _turns);
            }
        }
    }

    private sealed class ChatTurn
    {
        public required string Owner { get; init; }
        public required DateTime CreatedAt { get; init; }
        public string Status { get; set; } = "running";
        public List<TurnTool> Tools { get; init; } = new();
    }

    private TurnTool DescribeTurnTool(TurnTool tool)
    {
        if (tool.OperationId != null)
            return tool with { Status = GetLocalApprovalStatus(tool.OperationId) ?? tool.Status };
        if (string.IsNullOrEmpty(tool.TaskId)) return tool;
        var task = MSLX.SDK.MSLX.Tasks?.GetTask(tool.TaskId);
        return tool with
        {
            Status = task?.State switch
            {
                MSLX.SDK.Models.Files.TaskState.Pending => "submitted",
                MSLX.SDK.Models.Files.TaskState.Running => "running",
                MSLX.SDK.Models.Files.TaskState.Success => "succeeded",
                MSLX.SDK.Models.Files.TaskState.Failed => "failed",
                MSLX.SDK.Models.Files.TaskState.Canceled => "cancelled",
                _ => tool.Status
            }
        };
    }

    private sealed record TurnTool(string Tool, string Status, string? TaskId, string? OperationId);
}
