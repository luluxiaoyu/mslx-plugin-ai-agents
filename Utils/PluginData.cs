using System.Text.Json;
using MSLX.SDK;

namespace MSLX.Plugin.AiAgents.Utils;

internal static class PluginData
{
    public static string DirectoryPath => MSLXPluginEntry.Instance.Config().GetDataPath();

    public static void MigrateLegacy(string directory)
    {
        var legacy = Path.Combine(Path.GetDirectoryName(directory)!, "mslx-plugins-ai-agents");
        foreach (var file in new[] { "Config.json", "mcp_tokens.json" })
        {
            var source = Path.Combine(legacy, file);
            var destination = Path.Combine(directory, file);
            if (!File.Exists(destination) && File.Exists(source))
                File.Copy(source, destination);
        }
    }

    public static void WriteJson<T>(string destination, T data)
    {
        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
