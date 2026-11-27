using System.Text.Json;
using MSLX.Plugin.AiAgents.Models;

namespace MSLX.Plugin.AiAgents.Services;

internal static class McpToolPermissions
{
    internal static bool AllowsTool(McpTokenRecord token, McpToolDefinition tool) =>
        (!token.ReadOnly || IsReadOnly(tool.Name) || IsMixed(tool.Name)) &&
        (token.ServerIds == null || tool.Name is "query_operation_status" or "query_background_task" or "cancel_background_task" ||
         (tool.InputSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("server_id", out _)));

    internal static bool AllowsArguments(McpTokenRecord token, string name, JsonElement arguments)
    {
        var action = McpRequest.GetString(arguments, "action")?.Trim().ToLowerInvariant();
        if (token.ReadOnly && !IsReadOnly(name) &&
            !(IsMixed(name) && (name == "control_frp_tunnel" ? action is "log" or "logs" : action == "list")))
            return false;

        if (token.ServerIds != null && name == "manage_scheduled_tasks" && action is "delete" or "toggle")
        {
            var taskId = McpRequest.GetString(arguments, "task_id");
            if (string.IsNullOrEmpty(taskId)) return false;
            var task = MSLX.SDK.MSLX.Config.Tasks.GetTask(taskId);
            if (task == null || !token.ServerIds.Contains((int)task.InstanceId)) return false;
        }

        if (token.ServerIds != null && name is "query_background_task" or "cancel_background_task")
        {
            var bgTaskId = McpRequest.GetString(arguments, "task_id");
            if (string.IsNullOrEmpty(bgTaskId)) return false;
            var bgTask = MSLX.SDK.MSLX.Tasks?.GetTask(bgTaskId);
            return bgTask != null && token.ServerIds.Contains((int)bgTask.InstanceId);
        }

        return token.ServerIds == null || name == "query_operation_status" ||
            (arguments.TryGetProperty("server_id", out var target) &&
             int.TryParse(target.ToString(), out var id) && token.ServerIds.Contains(id));
    }

    private static bool IsMixed(string name) => name is "manage_whitelist" or "manage_ops" or "manage_bans"
        or "manage_mod_plugin_state" or "manage_scheduled_tasks" or "control_frp_tunnel";

    private static bool IsReadOnly(string name) => name is "list_server_files" or "read_server_file"
        or "query_creation_status" or "query_java_environments" or "query_available_server_cores"
        or "query_server_status" or "read_server_log" or "query_system_metrics" or "list_server_mods_plugins"
        or "query_players" or "list_server_backups" or "query_frp_status" or "search_mc_resources"
        or "query_resource_versions" or "audit_client_only_mods" or "query_python_environments"
        or "query_operation_status" or "list_background_tasks" or "query_background_task";
}
