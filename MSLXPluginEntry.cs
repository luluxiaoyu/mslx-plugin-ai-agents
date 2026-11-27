using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MSLX.Plugin.AiAgents.Controllers;
using MSLX.Plugin.AiAgents.Hubs;
using MSLX.Plugin.AiAgents.Services;
using MSLX.SDK;
using MSLX.SDK.Interfaces;
using MSLX.SDK.Models.Plugins;

[assembly: ApplicationPart("MSLX.Plugin.AiAgents")]

namespace MSLX.Plugin.AiAgents;

public class MSLXPluginEntry : IPlugin
{
    public static MSLXPluginEntry Instance { get; private set; } = null!;
    public static IServiceProvider? ServiceProvider { get; private set; }
    public string Id => "mslx-plugin-ai-agents";
    public string Name => "AI 助手";
    public string Description => "支持在线对话 & MCP 服务的 AI 助手插件，可让AI助手帮您操作 MSLX。";
    public string Version => "1.1.0";
    public string Icon => "f06867909c1984881489196158935cf0_3785578587551796133.png";
    public string MinSDKVersion => "1.7.9";
    public string Developer => "luluxiaoyu";
    public string AuthorUrl => "https://github.com/luluxiaoyu/mslx-plugin-ai-agents";
    public string PluginUrl => "https://mslx-plugins.mslmc.net/plugins/mslx-plugin-ai-agents";
    public PluginMultiNodeSupport SupportsMultiNode => PluginMultiNodeSupport.Supported;

    public void OnPluginInitialize(IServiceProvider serviceProvider)
    {
        Instance = this;
        ServiceProvider = serviceProvider;
        // The host materializes an empty Config.json when its plugin bridge is first opened.
        var dataDir = Path.Combine(SDK.MSLX.Config.GetAppDataPath(), "PluginsData", Id);
        Directory.CreateDirectory(dataDir);
        Utils.PluginData.MigrateLegacy(dataDir);
        SDK.MSLX.Logger.Info("mslx-plugin-ai-agents 初始化成功~");
    }

    public void OnLoad()
    {
        SDK.MSLX.Logger.Info("mslx-plugin-ai-agents 载入成功~");
    }

    public void OnUnload()
    {
        McpController.CloseLegacySessions();
        ServiceProvider?.GetService<AiService>()?.StopAllChatTurns();
        ServiceProvider?.GetService<McpTokenManager>()?.Dispose();
        ServiceProvider = null;
        SDK.MSLX.Logger.Info("mslx-plugin-ai-agents 卸载成功~");
    }

    public void OnRegisterEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<AiChatHub>("/api/hubs/plugin/mslx-plugin-ai-agents/chat");
    }

    public void OnRegisterServices(IServiceCollection services)
    {
        services.AddSingleton<McpUploadService>();
        services.AddSingleton<ServerTerminalService>();
        services.AddSingleton<AiService>();
        services.AddSingleton<McpTokenManager>();
    }
}
