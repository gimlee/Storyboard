using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Storyboard.AI;
using Storyboard.AI.Core;
using Storyboard.Infrastructure.Configuration;
using Storyboard.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Storyboard.ViewModels;

public partial class ApiKeyViewModel : ObservableObject
{
    private readonly AIConfigurationComposer _configComposer;
    private readonly UserSettingsStore _userSettingsStore;
    private readonly AIServiceManager _aiManager;

    public event EventHandler? CloseRequested;

    public ApiKeyViewModel(
        AIConfigurationComposer configComposer,
        UserSettingsStore userSettingsStore,
        AIServiceManager aiManager)
    {
        _configComposer = configComposer;
        _userSettingsStore = userSettingsStore;
        _aiManager = aiManager;

        AvailableProviderTypes = Enum.GetValues(typeof(AIProviderType)).Cast<AIProviderType>().ToList();

        ValidationResults.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(HasValidationResults));
            OnPropertyChanged(nameof(HasNoValidationResults));
        };

        LoadFromFile();
    }

    public IReadOnlyList<AIProviderType> AvailableProviderTypes { get; }

    public ObservableCollection<ProviderValidationResult> ValidationResults { get; } = new();
    public bool HasValidationResults => ValidationResults.Count > 0;
    public bool HasNoValidationResults => ValidationResults.Count == 0;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private AIProviderType _defaultTextProvider = AIProviderType.Qwen;

    [ObservableProperty]
    private string _defaultTextModel = string.Empty;

    [ObservableProperty]
    private AIProviderType _defaultImageProvider = AIProviderType.Qwen;

    [ObservableProperty]
    private string _defaultImageModel = string.Empty;

    [ObservableProperty]
    private AIProviderType _defaultVideoProvider = AIProviderType.Qwen;

    [ObservableProperty]
    private string _defaultVideoModel = string.Empty;

    [ObservableProperty]
    private AIProviderType _defaultTtsProvider = AIProviderType.Qwen;

    [ObservableProperty]
    private string _defaultTtsModel = string.Empty;

    [ObservableProperty]
    private AIProviderType _selectedProvider = AIProviderType.NewApi;

    [ObservableProperty] private bool _qwenEnabled;
    [ObservableProperty] private string _qwenApiKey = string.Empty;
    [ObservableProperty] private string _qwenEndpoint = string.Empty;
    [ObservableProperty] private int _qwenTimeoutSeconds = 120;

    [ObservableProperty] private bool _volcengineEnabled;
    [ObservableProperty] private string _volcengineApiKey = string.Empty;
    [ObservableProperty] private string _volcengineEndpoint = string.Empty;
    [ObservableProperty] private int _volcengineTimeoutSeconds = 120;
    [ObservableProperty] private string _volcengineTtsAppId = string.Empty;
    [ObservableProperty] private string _volcengineTtsCluster = "volcano_tts";

    [ObservableProperty] private bool _newApiEnabled;
    [ObservableProperty] private string _newApiApiKey = string.Empty;
    [ObservableProperty] private string _newApiEndpoint = string.Empty;
    [ObservableProperty] private int _newApiTimeoutSeconds = 120;

    [ObservableProperty] private bool _klingEnabled;
    [ObservableProperty] private string _klingApiKey = string.Empty;
    [ObservableProperty] private string _klingEndpoint = string.Empty;
    [ObservableProperty] private int _klingTimeoutSeconds = 180;

    [ObservableProperty] private bool _deepSeekEnabled;
    [ObservableProperty] private string _deepSeekApiKey = string.Empty;
    [ObservableProperty] private string _deepSeekEndpoint = string.Empty;
    [ObservableProperty] private int _deepSeekTimeoutSeconds = 120;

    public bool IsQwenSelected => SelectedProvider == AIProviderType.Qwen;
    public bool IsVolcengineSelected => SelectedProvider == AIProviderType.Volcengine;
    public bool IsNewApiSelected => SelectedProvider == AIProviderType.NewApi;
    public bool IsKlingSelected => SelectedProvider == AIProviderType.Kling;
    public bool IsDeepSeekSelected => SelectedProvider == AIProviderType.DeepSeek;

    private void LoadFromFile()
    {
        var cfg = _configComposer.LoadConfiguration();

        DefaultTextProvider = cfg.Defaults.Text.Provider;
        DefaultTextModel = cfg.Defaults.Text.Model;
        DefaultImageProvider = cfg.Defaults.Image.Provider;
        DefaultImageModel = cfg.Defaults.Image.Model;
        DefaultVideoProvider = cfg.Defaults.Video.Provider;
        DefaultVideoModel = cfg.Defaults.Video.Model;
        DefaultTtsProvider = cfg.Defaults.Tts.Provider;
        DefaultTtsModel = cfg.Defaults.Tts.Model;

        SelectedProvider = AIProviderType.NewApi;

        var qwen = cfg.Providers.Qwen;
        QwenEnabled = qwen.Enabled;
        QwenApiKey = qwen.ApiKey;
        QwenEndpoint = qwen.Endpoint;
        QwenTimeoutSeconds = qwen.TimeoutSeconds;

        var volc = cfg.Providers.Volcengine;
        VolcengineEnabled = volc.Enabled;
        VolcengineApiKey = volc.ApiKey;
        VolcengineEndpoint = volc.Endpoint;
        VolcengineTimeoutSeconds = volc.TimeoutSeconds;
        VolcengineTtsAppId = cfg.Tts.Volcengine.AppId ?? string.Empty;
        VolcengineTtsCluster = string.IsNullOrWhiteSpace(cfg.Tts.Volcengine.Cluster) ? "volcano_tts" : cfg.Tts.Volcengine.Cluster;

        var newApi = cfg.Providers.NewApi;
        NewApiEnabled = newApi.Enabled;
        NewApiApiKey = newApi.ApiKey;
        NewApiEndpoint = newApi.Endpoint;
        NewApiTimeoutSeconds = newApi.TimeoutSeconds;

        var kling = cfg.Providers.Kling;
        KlingEnabled = kling.Enabled;
        KlingApiKey = kling.ApiKey;
        KlingEndpoint = kling.Endpoint;
        KlingTimeoutSeconds = kling.TimeoutSeconds;

        var deepSeek = cfg.Providers.DeepSeek;
        DeepSeekEnabled = deepSeek.Enabled;
        DeepSeekApiKey = deepSeek.ApiKey;
        DeepSeekEndpoint = deepSeek.Endpoint;
        DeepSeekTimeoutSeconds = deepSeek.TimeoutSeconds;
    }

    partial void OnSelectedProviderChanged(AIProviderType value)
    {
        OnPropertyChanged(nameof(IsQwenSelected));
        OnPropertyChanged(nameof(IsVolcengineSelected));
        OnPropertyChanged(nameof(IsNewApiSelected));
        OnPropertyChanged(nameof(IsKlingSelected));
        OnPropertyChanged(nameof(IsDeepSeekSelected));
    }

    partial void OnDefaultTextProviderChanged(AIProviderType value)
    {
        DefaultTextModel = GetProviderDefaultModel(value, "Text");
    }

    partial void OnDefaultImageProviderChanged(AIProviderType value)
    {
        DefaultImageModel = GetProviderDefaultModel(value, "Image");
    }

    partial void OnDefaultVideoProviderChanged(AIProviderType value)
    {
        DefaultVideoModel = GetProviderDefaultModel(value, "Video");
    }

    partial void OnDefaultTtsProviderChanged(AIProviderType value)
    {
        DefaultTtsModel = GetProviderDefaultModel(value, "Tts");
    }

    private string GetProviderDefaultModel(AIProviderType providerType, string modelType)
    {
        var cfg = _configComposer.LoadConfiguration();
        var providerConfig = providerType switch
        {
            AIProviderType.Qwen => cfg.Providers.Qwen,
            AIProviderType.Volcengine => cfg.Providers.Volcengine,
            AIProviderType.NewApi => cfg.Providers.NewApi,
            AIProviderType.Kling => cfg.Providers.Kling,
            AIProviderType.DeepSeek => cfg.Providers.DeepSeek,
            _ => cfg.Providers.Qwen
        };

        return modelType switch
        {
            "Text" => providerConfig.DefaultModels.Text,
            "Image" => providerConfig.DefaultModels.Image,
            "Video" => providerConfig.DefaultModels.Video,
            "Tts" => providerConfig.DefaultModels.Tts,
            _ => string.Empty
        };
    }

    [RelayCommand]
    private void Reload()
    {
        LoadFromFile();
        StatusMessage = "已从 user.ai.settings.json 重新加载。";
    }

    [RelayCommand]
    private void Save()
    {
        if (TrySave(out var error))
        {
            StatusMessage = "配置已保存到 user.ai.settings.json。";
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        StatusMessage = $"保存失败：{error}";
    }

    private bool TrySave(out string? error)
    {
        try
        {
            var qwenConfig = BuildProviderUserConfig(
                QwenApiKey,
                QwenEnabled,
                QwenEndpoint,
                QwenTimeoutSeconds);

            var volcConfig = BuildProviderUserConfig(
                VolcengineApiKey,
                VolcengineEnabled,
                VolcengineEndpoint,
                VolcengineTimeoutSeconds,
                VolcengineTtsAppId,
                VolcengineTtsCluster);

            var newApiConfig = BuildProviderUserConfig(
                NewApiApiKey,
                NewApiEnabled,
                NewApiEndpoint,
                NewApiTimeoutSeconds);

            var klingConfig = BuildProviderUserConfig(
                KlingApiKey,
                KlingEnabled,
                KlingEndpoint,
                KlingTimeoutSeconds);

            var deepSeekConfig = BuildProviderUserConfig(
                DeepSeekApiKey,
                DeepSeekEnabled,
                DeepSeekEndpoint,
                DeepSeekTimeoutSeconds);

            _configComposer.SaveUserConfiguration("Qwen", qwenConfig);
            _configComposer.SaveUserConfiguration("Volcengine", volcConfig);
            _configComposer.SaveUserConfiguration("NewApi", newApiConfig);
            _configComposer.SaveUserConfiguration("Kling", klingConfig);
            _configComposer.SaveUserConfiguration("DeepSeek", deepSeekConfig);

            var userSettings = _userSettingsStore.Load();
            userSettings.DefaultProviders.TextProvider = DefaultTextProvider.ToString();
            userSettings.DefaultProviders.ImageProvider = DefaultImageProvider.ToString();
            userSettings.DefaultProviders.VideoProvider = DefaultVideoProvider.ToString();
            userSettings.DefaultProviders.TtsProvider = DefaultTtsProvider.ToString();
            userSettings.DefaultProviders.TextModel = DefaultTextModel?.Trim() ?? string.Empty;
            userSettings.DefaultProviders.ImageModel = DefaultImageModel?.Trim() ?? string.Empty;
            userSettings.DefaultProviders.VideoModel = DefaultVideoModel?.Trim() ?? string.Empty;
            userSettings.DefaultProviders.TtsModel = DefaultTtsModel?.Trim() ?? string.Empty;
            _userSettingsStore.Save(userSettings);

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static ProviderUserConfig BuildProviderUserConfig(
        string apiKey,
        bool enabled,
        string endpoint,
        int timeoutSeconds,
        string? appId = null,
        string? cluster = null)
    {
        return new ProviderUserConfig
        {
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
            Enabled = enabled,
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim(),
            TimeoutSeconds = timeoutSeconds,
            AppId = string.IsNullOrWhiteSpace(appId) ? null : appId.Trim(),
            Cluster = string.IsNullOrWhiteSpace(cluster) ? null : cluster.Trim()
        };
    }

    [RelayCommand]
    private async Task ValidateAsync()
    {
        ValidationResults.Clear();

        if (!TrySave(out var saveError))
        {
            StatusMessage = $"验证前保存失败：{saveError}";
            return;
        }

        try
        {
            StatusMessage = "正在验证提供者配置...";
            var results = await _aiManager.ValidateAllProvidersAsync().ConfigureAwait(false);

            foreach (var kv in results.OrderBy(kv => kv.Key))
            {
                ValidationResults.Add(new ProviderValidationResult
                {
                    Provider = kv.Key,
                    Success = kv.Value,
                    Message = kv.Value ? "配置有效" : "配置无效",
                    Timestamp = DateTimeOffset.Now
                });
            }

            StatusMessage = "验证完成。";
        }
        catch (Exception ex)
        {
            ValidationResults.Add(new ProviderValidationResult
            {
                Provider = DefaultTextProvider,
                Success = false,
                Message = $"验证错误：{ex.Message}",
                Timestamp = DateTimeOffset.Now
            });
            StatusMessage = "验证失败。";
        }
    }
}
