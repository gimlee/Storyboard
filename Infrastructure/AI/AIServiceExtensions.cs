using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Storyboard.AI.Core;
using Storyboard.AI.Prompts;
using Storyboard.AI.Providers;
using Storyboard.Infrastructure.Configuration;
using Storyboard.Infrastructure.Media;
using Storyboard.Infrastructure.Media.Providers;

namespace Storyboard.AI;

public static class AIServiceExtensions
{
    public static IServiceCollection AddAIServices(this IServiceCollection services)
    {
        services.AddSingleton<UserSettingsStore>();
        services.AddSingleton<UserAIOverridesStore>();
        services.AddSingleton<AIConfigurationComposer>();
        services.AddSingleton<IOptionsMonitor<AIServicesConfiguration>>(sp =>
        {
            var composer = sp.GetRequiredService<AIConfigurationComposer>();
            return new SimpleOptionsMonitor<AIServicesConfiguration>(composer);
        });

        services.AddSingleton<IAIServiceProvider, QwenServiceProvider>();
        services.AddSingleton<IAIServiceProvider, VolcengineServiceProvider>();
        services.AddSingleton<IAIServiceProvider, NewApiServiceProvider>();
        services.AddSingleton<IAIServiceProvider, DeepSeekServiceProvider>();
        services.AddSingleton<IImageGenerationProvider, QwenImageGenerationProvider>();
        services.AddSingleton<IImageGenerationProvider, VolcengineImageGenerationProvider>();
        services.AddSingleton<IImageGenerationProvider, NewApiImageGenerationProvider>();
        services.AddSingleton<IImageGenerationProvider, KlingImageGenerationProvider>();
        services.AddSingleton<IVideoGenerationProvider, QwenVideoGenerationProvider>();
        services.AddSingleton<IVideoGenerationProvider, VolcengineVideoGenerationProvider>();
        services.AddSingleton<IVideoGenerationProvider, NewApiVideoGenerationProvider>();
        services.AddSingleton<IVideoGenerationProvider, KlingVideoGenerationProvider>();
        services.AddSingleton<PromptManagementService>();
        services.AddSingleton<AIServiceManager>();

        return services;
    }

    public static IServiceCollection AddAIServices(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration _)
    {
        return AddAIServices(services);
    }
}
