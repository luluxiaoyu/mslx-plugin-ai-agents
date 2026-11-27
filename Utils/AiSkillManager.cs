using System.Reflection;
using System.Text;
using MSLX.Plugin.AiAgents.Models;

namespace MSLX.Plugin.AiAgents.Utils;

public static class AiSkillManager
{
    private static readonly string[] SharedSkills =
    [
        "SKILL.md", "ServerOps.md", "ResourceOps.md", "BackupAndRecovery.md", "FrpOps.md",
        "PlayerOps.md", "JavaMatrix.md", "FileSafety.md"
    ];

    private const string PublicSummary = "先调用 tools/list，从实际提供的 schema 选择工具。执行删除或覆盖时，使用工具返回的操作编号，引导管理员到当前节点的 MCP 页确认。";

    public static string GetSystemPrompt() =>
        string.Join("\n\n", new[] { Read("SystemPrompt.md"), Read("ChatApprovals.md"), string.Join("\n\n", SharedSkills.Select(Read)) }.Where(text => text.Length > 0));

    public static string GetSkillMarkdown(IEnumerable<McpToolDefinition> tools)
    {
        var markdown = new StringBuilder(ReadSharedSkills());
        var protocol = Read("McpProtocol.md");
        if (protocol.Length > 0) markdown.AppendLine().AppendLine().AppendLine(protocol);
        markdown.AppendLine().AppendLine("## MCP 工具目录").AppendLine();
        foreach (var tool in tools)
            markdown.AppendLine($"- `{tool.Name}` · {tool.Description}");
        return markdown.ToString();
    }

    private static string ReadSharedSkills() =>
        string.Join("\n\n", SharedSkills.Select(Read).Append(PublicSummary).Where(text => text.Length > 0));

    private static string Read(string file)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(resource => resource.EndsWith($".Skills.{file}", StringComparison.Ordinal));
            if (name != null && assembly.GetManifestResourceStream(name) is { } stream)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var content = reader.ReadToEnd().Trim();
                if (content.Length > 0) return content;
            }
            var path = Path.Combine(PluginData.DirectoryPath, "Skills", file);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MSLX.SDK.MSLX.Logger.Error($"无法加载 AI 运维说明 {file}: {ex.Message}");
            return "";
        }
    }
}
