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
    public string Id => "mslx-plugins-ai-agents";
    public string Name => "AI 助手";
    public string Description => "基于大语言模型的 Minecraft 服务器智能运维与对话助手";
    public string Version => "1.0.0";
    public string Icon => "icon.png";
    public string MinSDKVersion => "1.7.6";
    public string Developer => "luluxiaoyu";
    public string AuthorUrl => "https://github.com/luluxiaoyu";
    public string PluginUrl => "https://github.com/MSLTeam/mslx-plugins-ai-agents";
    public PluginMultiNodeSupport SupportsMultiNode => PluginMultiNodeSupport.Supported;

    public void OnPluginInitialize(IServiceProvider serviceProvider)
    {
        Instance = this;
        ServiceProvider = serviceProvider;
        string dataDir = this.Config().GetDataPath();
        if (!Directory.Exists(dataDir))
        {
            Directory.CreateDirectory(dataDir);
        }
        SDK.MSLX.Logger.Info("mslx-plugins-ai-agents 初始化成功~");
    }

    public void OnLoad()
    {
        SDK.MSLX.Logger.Info("mslx-plugins-ai-agents 载入成功~");
    }

    public void OnUnload()
    {
        McpController.CloseLegacySessions();
        SDK.MSLX.Logger.Info("mslx-plugins-ai-agents 卸载成功~");
    }

    public void OnRegisterEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<AiChatHub>("/api/hubs/plugin/mslx-plugins-ai-agents/chat");
    }

    public void OnRegisterServices(IServiceCollection services)
    {
        services.AddSingleton<AiService>();
        services.AddSingleton<McpTokenManager>();
    }
}
