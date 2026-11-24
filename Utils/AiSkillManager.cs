using System.Reflection;
using System.Text;
using MSLX.Plugin.AiAgents.Models;

namespace MSLX.Plugin.AiAgents.Utils;

public static class AiSkillManager
{
    public static string GetSystemPrompt()
    {
        var sb = new StringBuilder();
        var loadedCount = 0;

        // 1. 优先从 Assembly 程序集内嵌资源中读取 (支持嵌入资源)
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames()
                .Where(r => r.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && r.Contains("Skills", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r)
                .ToList();

            foreach (var resourceName in resourceNames)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var content = reader.ReadToEnd().Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        sb.AppendLine(content);
                        sb.AppendLine();
                        loadedCount++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AiSkillManager] 读取程序集内嵌 Skill 资源异常: {ex.Message}");
        }

        // 2. 如果内嵌资源未读取到，降级尝试从磁盘 Skills 目录读取
        if (loadedCount == 0)
        {
            var skillDiskPaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Skills"),
                Path.Combine(Directory.GetCurrentDirectory(), "Skills"),
                Path.Combine(Directory.GetCurrentDirectory(), "plugins", "mslx-plugins-ai-agents", "Skills"),
                Path.Combine(Directory.GetCurrentDirectory(), "Plugins", "mslx-plugins-ai-agents", "Skills"),
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "Skills")
            };

            foreach (var dirPath in skillDiskPaths)
            {
                var fullDirPath = Path.GetFullPath(dirPath);
                if (!Directory.Exists(fullDirPath)) continue;

                var skillFiles = Directory.GetFiles(fullDirPath, "*.md", SearchOption.TopDirectoryOnly);
                Array.Sort(skillFiles);

                foreach (var file in skillFiles)
                {
                    try
                    {
                        var fullFilePath = Path.GetFullPath(file);
                        if (!fullFilePath.StartsWith(fullDirPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var content = File.ReadAllText(fullFilePath, Encoding.UTF8).Trim();
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            sb.AppendLine(content);
                            sb.AppendLine();
                            loadedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AiSkillManager] 读取磁盘 Skill 文件 {file} 异常: {ex.Message}");
                    }
                }

                if (loadedCount > 0) break;
            }
        }

        if (loadedCount == 0 || sb.Length == 0)
        {
            return """
你是一个高效、果断的 Minecraft 服务器运维助手。当用户提出需求时，优先调用工具函数(Function Calling)完成操作。
""";
        }

        return sb.ToString().Trim();
    }

    public static string GetSkillMarkdown(IEnumerable<McpToolDefinition> tools)
    {
        var markdown = new StringBuilder(ReadSkillMarkdown());
        markdown.AppendLine().AppendLine();
        markdown.AppendLine("## MCP 工具目录").AppendLine();
        foreach (var tool in tools)
        {
            markdown.AppendLine($"- `{tool.Name}` · {tool.Description}");
        }

        return markdown.ToString();
    }

    private static string ReadSkillMarkdown()
    {
        // 1. 尝试从内嵌资源读取 SKILL.md
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var skillResName = assembly.GetManifestResourceNames()
                .FirstOrDefault(r => r.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(skillResName))
            {
                using var stream = assembly.GetManifestResourceStream(skillResName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var text = reader.ReadToEnd().Trim();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }
        catch { }

        // 2. 尝试从磁盘路径读取 SKILL.md
        var skillDiskPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Skills", "SKILL.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "Skills", "SKILL.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "plugins", "mslx-plugins-ai-agents", "Skills", "SKILL.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "Plugins", "mslx-plugins-ai-agents", "Skills", "SKILL.md"),
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "Skills", "SKILL.md")
        };

        foreach (var p in skillDiskPaths)
        {
            if (File.Exists(p))
            {
                try
                {
                    var text = File.ReadAllText(p, Encoding.UTF8).Trim();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                catch { }
            }
        }

        // 3. 回退为 GetSystemPrompt()
        return GetSystemPrompt();
    }
}
