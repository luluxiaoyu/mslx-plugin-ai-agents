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

    private const string FullPermissionChatNotice = """
        # ⚠️ 完全权限模式已开启（必须极其慎重操作）

        当前管理员已开启「完全权限模式」，**所有工具操作（包括删除实例、删除或覆盖文件、还原或删除备份、替换核心、安装插件、执行实例终端命令行等）均不再需要审批确认卡片，调用后将立即执行**！

        1. 执行任何高风险、不可逆或可能影响服务器稳定与存档安全的行为前，**必须先在对话中向用户清晰说明将要执行的具体操作、目标对象与潜在风险，并明确征得用户同意后再调用工具**。
        2. 若用户尚未明确同意具体高危操作，禁止直接删除/覆盖关键数据或执行破坏性终端命令。
        """;

    private const string FullPermissionMcpNotice = """
        > [!CAUTION]
        > **当前已开启「完全权限模式」**：所有写操作、删除、覆盖、还原及实例终端命令行均**无需管理员在 MCP 面板审批**，调用后将立即执行！
        > 调用任何高危或不可逆工具（如删除实例、删除/覆盖重要文件、还原备份、执行终端命令）前，**务必极其慎重，先向用户说明风险与具体操作内容并征得用户明确同意后再执行**。
        """;

    public static string GetSystemPrompt(bool fullPermissionMode = false)
    {
        var approvalSection = fullPermissionMode ? FullPermissionChatNotice : Read("ChatApprovals.md");
        return string.Join("\n\n", new[] { Read("SystemPrompt.md"), approvalSection, string.Join("\n\n", SharedSkills.Select(Read)) }.Where(text => text.Length > 0));
    }

    public static string GetSkillMarkdown(IEnumerable<McpToolDefinition> tools, bool fullPermissionMode = false)
    {
        var markdown = new StringBuilder();
        if (fullPermissionMode)
        {
            markdown.AppendLine(FullPermissionMcpNotice).AppendLine();
        }
        markdown.Append(ReadSharedSkills());
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
