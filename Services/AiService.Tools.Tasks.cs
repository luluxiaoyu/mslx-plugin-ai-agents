using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MSLX.SDK.IServices;
using MSLX.SDK.Models.Files;

namespace MSLX.Plugin.AiAgents.Services;

public partial class AiService
{
    private void RegisterBackgroundTaskTools(JsonArray tools)
    {
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "list_background_tasks",
                ["description"] = "查询当前用户的全局后台任务列表（包括压缩、解压、下载、导出、插件、建服、更新核心、备份还原、节点同步等异步任务），按创建时间倒序返回任务 ID、实例 ID、任务类型 (type)、标题 (title)、目标名 (targetName)、状态 (state)、进度百分比 (progress) 和状态信息 (message)。当触发备份/还原/建服等 SDK 接口未直接返回 taskId 时，可通过此工具结合实例 ID、任务类型、标题或目标名匹配定位后台任务进度。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_id"] = new JsonObject { ["type"] = "number", ["description"] = "可选：按目标服务器实例 ID 过滤（限定实例的 MCP 令牌必须传有权限的 server_id）" },
                        ["task_type"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "可选：按任务类型过滤：Compress、Decompress、Download、Export、Plugin、CreateServer、UpdateServer、Backup、NodeSync"
                        },
                        ["state"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "可选：按任务状态过滤：active（Pending 或 Running）、Pending、Running、Success、Failed、Canceled"
                        },
                        ["keyword"] = new JsonObject { ["type"] = "string", ["description"] = "可选：按任务标题、目标文件名或状态消息进行关键词过滤（不区分大小写）" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "可选：最多返回条数，默认 20，最大 100" }
                    }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "query_background_task",
                ["description"] = "根据任务 ID (task_id) 查询单个后台任务的实时进度、状态、消息及节点同步明细。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["task_id"] = new JsonObject { ["type"] = "string", ["description"] = "后台任务 ID" }
                    },
                    ["required"] = new JsonArray { "task_id" }
                }
            }
        });

        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "cancel_background_task",
                ["description"] = "尝试取消当前用户名下正在排队 (Pending) 或运行中 (Running) 的后台任务。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["task_id"] = new JsonObject { ["type"] = "string", ["description"] = "要取消的后台任务 ID" }
                    },
                    ["required"] = new JsonArray { "task_id" }
                }
            }
        });
    }

    private async Task<string?> HandleBackgroundTaskToolAsync(
        string name,
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        string userId)
    {
        switch (name)
        {
            case "list_background_tasks":
                return await HandleListBackgroundTasksAsync(args, onToolExecuted, userId);

            case "query_background_task":
                return await HandleQueryBackgroundTaskAsync(args, onToolExecuted, userId);

            case "cancel_background_task":
                return await HandleCancelBackgroundTaskAsync(args, onToolExecuted, userId);

            default:
                return null;
        }
    }

    private async Task<string> HandleListBackgroundTasksAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        string userId)
    {
        uint? instanceIdFilter = null;
        if (args?["server_id"] != null && uint.TryParse(args["server_id"]!.ToString(), out var parsedId) && parsedId > 0)
            instanceIdFilter = parsedId;

        string? taskTypeFilter = args?["task_type"]?.ToString()?.Trim();
        string? stateFilter = args?["state"]?.ToString()?.Trim();
        string? keyword = args?["keyword"]?.ToString()?.Trim();
        int limit = ParsePositiveIntOrDefault(args?["limit"], 20);
        limit = Math.Clamp(limit, 1, 100);

        var items = GetUserBackgroundTasks(userId, instanceIdFilter);
        if (items == null)
            return "[ERROR] 当前宿主环境未提供后台任务管理器接口，无法读取用户后台任务列表。";

        IEnumerable<BackgroundTaskItem> filtered = items;

        if (!string.IsNullOrEmpty(taskTypeFilter))
        {
            if (!Enum.TryParse<TaskType>(taskTypeFilter, ignoreCase: true, out var parsedType))
                return $"[ERROR] 无效的 task_type `{taskTypeFilter}`。可选：Compress、Decompress、Download、Export、Plugin、CreateServer、UpdateServer、Backup、NodeSync。";
            filtered = filtered.Where(t => t.Type == parsedType);
        }

        if (!string.IsNullOrEmpty(stateFilter))
        {
            if (stateFilter.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(t => t.State is TaskState.Pending or TaskState.Running);
            }
            else if (Enum.TryParse<TaskState>(stateFilter, ignoreCase: true, out var parsedState))
            {
                filtered = filtered.Where(t => t.State == parsedState);
            }
            else
            {
                return $"[ERROR] 无效的 state `{stateFilter}`。可选：active、Pending、Running、Success、Failed、Canceled。";
            }
        }

        if (!string.IsNullOrEmpty(keyword))
        {
            filtered = filtered.Where(t =>
                (t.Title?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.TargetName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Message?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
                t.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var matchedList = filtered.ToList();
        var page = matchedList.Take(limit).Select(DescribeBackgroundTask).ToArray();

        var payload = new
        {
            userId,
            instanceId = instanceIdFilter,
            totalMatched = matchedList.Count,
            returnedCount = page.Length,
            tasks = page
        };

        await onToolExecuted("list_background_tasks", payload);

        return $"[SUCCESS] 共匹配到 {matchedList.Count} 条后台任务（返回 {page.Length} 条）：\n{JsonSerializer.Serialize(payload)}";
    }

    private async Task<string> HandleQueryBackgroundTaskAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        string userId)
    {
        var taskId = args?["task_id"]?.ToString()?.Trim() ?? "";
        if (string.IsNullOrEmpty(taskId))
            return "[ERROR] task_id 不能为空。";

        var taskManager = ResolveBackgroundTaskManager();
        if (taskManager == null)
            return "[ERROR] 当前宿主环境未初始化后台任务管理器。";

        var task = taskManager.GetTask(taskId);
        if (task == null)
            return $"[ERROR] 未找到任务 {taskId}（可能不存在或已完成并超过保留期被清理）。";

        bool isMcp = userId.StartsWith("mcp:", StringComparison.Ordinal);
        if (isMcp && !string.IsNullOrEmpty(task.UserId) && !string.Equals(task.UserId, userId, StringComparison.Ordinal))
            return $"[ERROR] 当前令牌无权查看任务 {taskId}。";

        var info = DescribeBackgroundTask(task);
        await onToolExecuted("query_background_task", info);
        return $"[SUCCESS] 后台任务 {taskId} 状态如下：\n{JsonSerializer.Serialize(info)}";
    }

    private async Task<string> HandleCancelBackgroundTaskAsync(
        JsonObject? args,
        Func<string, object, Task> onToolExecuted,
        string userId)
    {
        var taskId = args?["task_id"]?.ToString()?.Trim() ?? "";
        if (string.IsNullOrEmpty(taskId))
            return "[ERROR] task_id 不能为空。";

        var taskManager = ResolveBackgroundTaskManager();
        if (taskManager == null)
            return "[ERROR] 当前宿主环境未初始化后台任务管理器。";

        bool isAdmin = !string.IsNullOrEmpty(userId) && !userId.StartsWith("mcp:", StringComparison.Ordinal);
        bool cancelled = taskManager.CancelTask(taskId, userId, isAdmin);
        var task = taskManager.GetTask(taskId);

        await onToolExecuted("cancel_background_task", new
        {
            taskId,
            cancelled,
            task = task != null ? DescribeBackgroundTask(task) : null
        });

        if (cancelled)
            return $"[SUCCESS] 已取消后台任务 {taskId}（{task?.Title ?? ""}）。";

        if (task == null)
            return $"[ERROR] 取消失败：未找到任务 {taskId}。";

        return $"[ERROR] 无法取消任务 {taskId}（当前状态：{task.State}，或无权限取消）。";
    }

    private IBackgroundTaskManager? ResolveBackgroundTaskManager()
    {
        try
        {
            return _serviceProvider.GetService(typeof(IBackgroundTaskManager)) as IBackgroundTaskManager
                ?? MSLX.SDK.MSLX.Tasks;
        }
        catch
        {
            return MSLX.SDK.MSLX.Tasks;
        }
    }

    private List<BackgroundTaskItem>? GetUserBackgroundTasks(string userId, uint? instanceId)
    {
        var taskManager = ResolveBackgroundTaskManager();
        if (taskManager == null) return null;

        try
        {
            var method = taskManager.GetType().GetMethod("GetUserTasks", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(uint?) }, null);
            if (method?.Invoke(taskManager, new object?[] { userId, instanceId }) is IEnumerable<BackgroundTaskItem> directList)
                return directList.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "调用宿主 GetUserTasks 失败");
        }

        return null;
    }

    private static object DescribeBackgroundTask(BackgroundTaskItem task) => new
    {
        id = task.Id,
        instanceId = task.InstanceId,
        type = task.Type.ToString(),
        title = task.Title,
        targetName = task.TargetName,
        state = task.State.ToString(),
        progress = task.Progress,
        message = task.Message,
        createdAt = task.CreatedAt,
        finishedAt = task.FinishedAt,
        syncResults = task.SyncResults
    };
}
