using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq; // 确保有 LINQ 引用，因为代码中用到了 .Where
using Microsoft.Extensions.Logging;
using Storyboard.AI.Core;

namespace Storyboard.Infrastructure.Configuration;

/// <summary>
/// AI 配置合成器
/// 核心公式: Effective = Validate(Schema, Defaults + UserOverrides)
/// </summary>
public class AIConfigurationComposer
{
    private readonly ILogger<AIConfigurationComposer> _logger;
    private readonly UserAIOverridesStore _overridesStore;
    private readonly UserSettingsStore _userSettingsStore;

    private AIDefaults? _defaults;
    private ProviderCapabilitySchema? _schema;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AIConfigurationComposer(
        ILogger<AIConfigurationComposer> logger,
        UserAIOverridesStore overridesStore,
        UserSettingsStore userSettingsStore)
    {
        _logger = logger;
        _overridesStore = overridesStore;
        _userSettingsStore = userSettingsStore;
    }

    /// <summary>
    /// 加载 AI 配置（合成最终有效配置）
    /// </summary>
    public AIServicesConfiguration LoadConfiguration()
    {
        // 1. 加载 Schema（结构定义）
        LoadSchema();

        // 2. 加载 Defaults（默认值）
        LoadDefaults();

        // 3. 加载 User Overrides（用户覆盖）
        var userOverrides = _overridesStore.Load();
        if (_schema != null && NormalizeUserOverrides(userOverrides))
        {
            _overridesStore.Save(userOverrides);
        }

        // 4. 合成最终配置
        return ComposeConfiguration(userOverrides);
    }

    /// <summary>
    /// 保存用户修改的配置，只保存差异
    /// </summary>
    public void SaveUserConfiguration(string providerName, ProviderUserConfig userConfig)
    {
        // 确保 defaults 已加载
        if (_defaults == null)
        {
            LoadDefaults();
        }

        if (_schema == null)
        {
            LoadSchema();
        }

        var overrides = _overridesStore.Load();
        var allowedFields = GetAllowedFields(providerName);

        // 获取默认配置
        ProviderDefaultConfig? defaultConfig = null;
        var hasDefaults = _defaults?.Providers.TryGetValue(providerName, out defaultConfig) == true;

        // 创建差异配置（只保存与默认值不同的部分）
        var diffConfig = new ProviderUserConfig();

        // ApiKey 始终保存（敏感信息，不在 defaults 中）
        if (IsAllowedField(allowedFields, "ApiKey") &&
            !string.IsNullOrWhiteSpace(userConfig.ApiKey))
        {
            diffConfig.ApiKey = userConfig.ApiKey;
        }

        // 只保存与默认值不同的 Enabled
        if (IsAllowedField(allowedFields, "Enabled") &&
            userConfig.Enabled.HasValue &&
            (!hasDefaults || userConfig.Enabled.Value != defaultConfig!.Enabled))
        {
            diffConfig.Enabled = userConfig.Enabled;
        }

        // 只保存与默认值不同的 Endpoint
        if (IsAllowedField(allowedFields, "Endpoint") &&
            !string.IsNullOrWhiteSpace(userConfig.Endpoint) &&
            (!hasDefaults || userConfig.Endpoint != defaultConfig!.Endpoint))
        {
            diffConfig.Endpoint = userConfig.Endpoint;
        }

        // 只保存与默认值不同的 TimeoutSeconds
        if (IsAllowedField(allowedFields, "TimeoutSeconds") &&
            userConfig.TimeoutSeconds.HasValue &&
            (!hasDefaults || userConfig.TimeoutSeconds.Value != defaultConfig!.TimeoutSeconds))
        {
            diffConfig.TimeoutSeconds = userConfig.TimeoutSeconds;
        }

        if (IsAllowedField(allowedFields, "AppId") &&
            !string.IsNullOrWhiteSpace(userConfig.AppId))
        {
            diffConfig.AppId = userConfig.AppId.Trim();
        }

        if (IsAllowedField(allowedFields, "Cluster") &&
            !string.IsNullOrWhiteSpace(userConfig.Cluster))
        {
            diffConfig.Cluster = userConfig.Cluster.Trim();
        }

        // 如果所有字段都为空（除了 ApiKey），则完全移除该 Provider 配置
        if (string.IsNullOrWhiteSpace(diffConfig.ApiKey) &&
            !diffConfig.Enabled.HasValue &&
            string.IsNullOrWhiteSpace(diffConfig.Endpoint) &&
            !diffConfig.TimeoutSeconds.HasValue &&
            string.IsNullOrWhiteSpace(diffConfig.AppId) &&
            string.IsNullOrWhiteSpace(diffConfig.Cluster))
        {
            overrides.Providers.Remove(providerName);
            _logger.LogInformation("用户配置已移除 (无差异): {Provider}", providerName);
        }
        else
        {
            overrides.Providers[providerName] = diffConfig;
            _logger.LogInformation("用户配置已保存 (仅差异): {Provider}", providerName);
        }

        _overridesStore.Save(overrides);
    }

    private HashSet<string>? GetAllowedFields(string providerName)
    {
        if (_schema == null)
            return null;

        if (!_schema.Providers.TryGetValue(providerName, out var providerDef))
            return null;

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in providerDef.RequiredFields)
        {
            allowed.Add(field);
        }
        foreach (var field in providerDef.OptionalFields)
        {
            allowed.Add(field);
        }
        return allowed;
    }

    private static bool IsAllowedField(HashSet<string>? allowedFields, string fieldName)
    {
        return allowedFields == null || allowedFields.Contains(fieldName);
    }

    private bool NormalizeUserOverrides(UserAIOverrides userOverrides)
    {
        if (_schema == null || _schema.Providers.Count == 0)
            return false;

        var changed = false;
        var schemaLookup = _schema.Providers.Keys
            .ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var providerKey in userOverrides.Providers.Keys.ToList())
        {
            if (!schemaLookup.TryGetValue(providerKey, out var canonical))
            {
                userOverrides.Providers.Remove(providerKey);
                changed = true;
                continue;
            }

            if (!string.Equals(providerKey, canonical, StringComparison.Ordinal))
            {
                if (!userOverrides.Providers.ContainsKey(canonical))
                {
                    userOverrides.Providers[canonical] = userOverrides.Providers[providerKey];
                }
                userOverrides.Providers.Remove(providerKey);
                changed = true;
            }
        }

        foreach (var (providerName, userConfig) in userOverrides.Providers.ToList())
        {
            if (!_schema.Providers.TryGetValue(providerName, out var providerDef))
            {
                userOverrides.Providers.Remove(providerName);
                changed = true;
                continue;
            }

            var allowedFields = new HashSet<string>(
                providerDef.RequiredFields.Concat(providerDef.OptionalFields),
                StringComparer.OrdinalIgnoreCase);

            if (!allowedFields.Contains("ApiKey") || string.IsNullOrWhiteSpace(userConfig.ApiKey))
            {
                if (userConfig.ApiKey != null)
                {
                    userConfig.ApiKey = null;
                    changed = true;
                }
            }

            if (!allowedFields.Contains("Enabled") && userConfig.Enabled.HasValue)
            {
                userConfig.Enabled = null;
                changed = true;
            }

            if (!allowedFields.Contains("Endpoint") || string.IsNullOrWhiteSpace(userConfig.Endpoint))
            {
                if (userConfig.Endpoint != null)
                {
                    userConfig.Endpoint = null;
                    changed = true;
                }
            }

            if (!allowedFields.Contains("TimeoutSeconds") && userConfig.TimeoutSeconds.HasValue)
            {
                userConfig.TimeoutSeconds = null;
                changed = true;
            }

            if (!allowedFields.Contains("AppId") || string.IsNullOrWhiteSpace(userConfig.AppId))
            {
                if (userConfig.AppId != null)
                {
                    userConfig.AppId = null;
                    changed = true;
                }
            }

            if (!allowedFields.Contains("Cluster") || string.IsNullOrWhiteSpace(userConfig.Cluster))
            {
                if (userConfig.Cluster != null)
                {
                    userConfig.Cluster = null;
                    changed = true;
                }
            }

            if (string.IsNullOrWhiteSpace(userConfig.ApiKey) &&
                !userConfig.Enabled.HasValue &&
                string.IsNullOrWhiteSpace(userConfig.Endpoint) &&
                !userConfig.TimeoutSeconds.HasValue &&
                string.IsNullOrWhiteSpace(userConfig.AppId) &&
                string.IsNullOrWhiteSpace(userConfig.Cluster))
            {
                userOverrides.Providers.Remove(providerName);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// 加载 Schema
    /// </summary>
    private void LoadSchema()
    {
        var schemaPath = ConfigurationPaths.ProviderCapabilitySchemaPath;

        if (!File.Exists(schemaPath))
        {
            _logger.LogWarning("Provider Capability Schema 文件不存在: {Path}", schemaPath);
            _schema = new ProviderCapabilitySchema { SchemaVersion = 1 };
            return;
        }

        try
        {
            var json = File.ReadAllText(schemaPath);
            _schema = JsonSerializer.Deserialize<ProviderCapabilitySchema>(json, JsonOptions)
                      ?? new ProviderCapabilitySchema();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 Provider Capability Schema 失败");
            _schema = new ProviderCapabilitySchema();
        }
    }

    /// <summary>
    /// 加载 Defaults
    /// </summary>
    private void LoadDefaults()
    {
        var defaultsPath = ConfigurationPaths.AIDefaultsPath;

        if (!File.Exists(defaultsPath))
        {
            _logger.LogWarning("AI Defaults 文件不存在: {Path}", defaultsPath);
            _defaults = new AIDefaults();
            return;
        }

        try
        {
            var json = File.ReadAllText(defaultsPath);
            _defaults = JsonSerializer.Deserialize<AIDefaults>(json, JsonOptions)
                        ?? new AIDefaults();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 AI Defaults 失败");
            _defaults = new AIDefaults();
        }
    }

    /// <summary>
    /// 合成最终配置
    /// </summary>
    private AIServicesConfiguration ComposeConfiguration(UserAIOverrides userOverrides)
    {
        var config = new AIServicesConfiguration();

        if (_defaults == null)
        {
            _logger.LogWarning("Defaults not loaded.");
            return config;
        }

        // 验证用户覆盖 (基于 Schema)
        if (_schema != null)
        {
            ValidateUserOverrides(userOverrides);
        }

        // 1. 合成每个 Provider 的配置
        var qwenConfig = ComposeProviderConfig("Qwen", userOverrides);
        var volcengineConfig = ComposeProviderConfig("Volcengine", userOverrides);
        var newApiConfig = ComposeProviderConfig("NewApi", userOverrides);
        var klingConfig = ComposeProviderConfig("Kling", userOverrides);
        var deepSeekConfig = ComposeProviderConfig("DeepSeek", userOverrides);

        config.Providers = new AIProvidersConfiguration
        {
            Qwen = qwenConfig,
            Volcengine = volcengineConfig,
            NewApi = newApiConfig,
            Kling = klingConfig,
            DeepSeek = deepSeekConfig
        };

        // 2. 设置 Defaults（Text/Image/Video 默认 Provider）
        var userSettings = _userSettingsStore.Load();
        var textProviderName = ResolveProviderName(userSettings.DefaultProviders.TextProvider, "Qwen");
        var imageProviderName = ResolveProviderName(userSettings.DefaultProviders.ImageProvider, "Volcengine");
        var videoProviderName = ResolveProviderName(userSettings.DefaultProviders.VideoProvider, "Volcengine");
        var ttsProviderName = ResolveProviderName(userSettings.DefaultProviders.TtsProvider, "NewApi");

        config.Defaults = new AIServiceDefaults
        {
            Text = new AIServiceDefaultSelection
            {
                Provider = ParseProviderType(textProviderName),
                Model = ResolveDefaultModel(userSettings.DefaultProviders.TextModel, textProviderName, "Text", userOverrides)
            },
            Image = new AIServiceDefaultSelection
            {
                Provider = ParseProviderType(imageProviderName),
                Model = ResolveDefaultModel(userSettings.DefaultProviders.ImageModel, imageProviderName, "Image", userOverrides)
            },
            Video = new AIServiceDefaultSelection
            {
                Provider = ParseProviderType(videoProviderName),
                Model = ResolveDefaultModel(userSettings.DefaultProviders.VideoModel, videoProviderName, "Video", userOverrides)
            },
            Tts = new AIServiceDefaultSelection
            {
                Provider = ParseProviderType(ttsProviderName),
                Model = ResolveDefaultModel(userSettings.DefaultProviders.TtsModel, ttsProviderName, "Tts", userOverrides)
            }
        };

        // 3. 合并 Image/Video 默认参数（见后续修复）
        config.Image = ComposeImageConfiguration();
        config.Video = ComposeVideoConfiguration();
        config.Image.DefaultProvider = MapImageProviderType(config.Defaults.Image.Provider);
        config.Video.DefaultProvider = MapVideoProviderType(config.Defaults.Video.Provider);
        config.Tts = ComposeTtsConfiguration(userOverrides);
        config.Tts.DefaultProvider = MapTtsProviderType(config.Defaults.Tts.Provider);

        return config;
    }

    /// <summary>
    /// 验证用户覆盖配置（基于 Schema）
    /// </summary>
    private void ValidateUserOverrides(UserAIOverrides userOverrides)
    {
        if (_schema == null)
            return;

        // 移除未知的 Provider
        var unknownProviders = userOverrides.Providers.Keys
            .Where(p => !_schema.Providers.ContainsKey(p))
            .ToList();

        foreach (var unknownProvider in unknownProviders)
        {
            _logger.LogWarning("移除未知的 Provider 配置: {Provider}", unknownProvider);
            userOverrides.Providers.Remove(unknownProvider);
        }

        // ???? Provider ?????
        foreach (var (providerName, userConfig) in userOverrides.Providers)
        {
            if (!_schema.Providers.TryGetValue(providerName, out var providerDef))
                continue;

            bool? enabled = userConfig.Enabled;
            if (!enabled.HasValue && _defaults?.Providers.TryGetValue(providerName, out var defaultConfig) == true)
            {
                enabled = defaultConfig.Enabled;
            }

            if (enabled == false)
            {
                continue;
            }

            // ?? ApiKey ????????????????
            if (providerDef.RequiredFields.Contains("ApiKey") &&
                string.IsNullOrWhiteSpace(userConfig.ApiKey))
            {
                _logger.LogWarning("{Provider} ??????: ApiKey", providerName);
            }
        }
    }

    /// <summary>
    /// 合成单个 Provider 的配置
    /// </summary>
    private AIProviderConfiguration ComposeProviderConfig(string providerName, UserAIOverrides userOverrides)
    {
        var providerConfig = new AIProviderConfiguration();

        // 从 defaults 获取基础配置
        if (_defaults?.Providers.TryGetValue(providerName, out var defaultConfig) == true)
        {
            providerConfig.Endpoint = defaultConfig.Endpoint;
            providerConfig.TimeoutSeconds = defaultConfig.TimeoutSeconds;
            providerConfig.Enabled = defaultConfig.Enabled;
            providerConfig.DefaultModels = new AIProviderModelDefaults
            {
                Text = defaultConfig.DefaultModels.TryGetValue("Text", out var text) ? text : "",
                Image = defaultConfig.DefaultModels.TryGetValue("Image", out var image) ? image : "",
                Video = defaultConfig.DefaultModels.TryGetValue("Video", out var video) ? video : "",
                Tts = defaultConfig.DefaultModels.TryGetValue("Tts", out var tts) ? tts : ""
            };
        }

        // 应用用户覆盖
        if (userOverrides.Providers.TryGetValue(providerName, out var userConfig))
        {
            if (userConfig.ApiKey != null)
                providerConfig.ApiKey = userConfig.ApiKey;

            if (userConfig.Enabled.HasValue)
                providerConfig.Enabled = userConfig.Enabled.Value;

            if (userConfig.Endpoint != null)
                providerConfig.Endpoint = userConfig.Endpoint;

            if (userConfig.TimeoutSeconds.HasValue)
                providerConfig.TimeoutSeconds = userConfig.TimeoutSeconds.Value;

        }

        return providerConfig;
    }

    /// <summary>
    /// 合成 Image 配置
    /// </summary>
    private ImageServicesConfiguration ComposeImageConfiguration()
    {
        var config = new ImageServicesConfiguration();

        if (_defaults?.Image?.Providers.TryGetValue("Qwen", out var qwenDefaults) == true)
        {
            config.Qwen = new QwenImageConfig
            {
                Size = qwenDefaults.Size,
                ResponseFormat = qwenDefaults.ResponseFormat,
                Watermark = qwenDefaults.Watermark,
                Stream = qwenDefaults.Stream,
                Images = qwenDefaults.Images,
                PromptExtend = qwenDefaults.PromptExtend
            };
        }

        if (_defaults?.Image?.Providers.TryGetValue("Volcengine", out var volcDefaults) == true)
        {
            config.Volcengine = new VolcengineImageConfig
            {
                Size = volcDefaults.Size,
                ResponseFormat = volcDefaults.ResponseFormat,
                Watermark = volcDefaults.Watermark,
                Stream = volcDefaults.Stream
            };
        }

        if (_defaults?.Image?.Providers.TryGetValue("NewApi", out var newApiDefaults) == true)
        {
            config.NewApi = new NewApiImageConfig
            {
                Size = newApiDefaults.Size,
                ResponseFormat = newApiDefaults.ResponseFormat,
                Watermark = newApiDefaults.Watermark,
                Stream = newApiDefaults.Stream,
                Images = newApiDefaults.Images,
                ProviderHint = newApiDefaults.ProviderHint
            };
        }

        if (_defaults?.Image?.Providers.TryGetValue("Kling", out var klingImgDefaults) == true)
        {
            config.Kling = new KlingImageConfig
            {
                AspectRatio = string.IsNullOrWhiteSpace(klingImgDefaults.AspectRatio) ? "16:9" : klingImgDefaults.AspectRatio!,
                ResponseFormat = string.IsNullOrWhiteSpace(klingImgDefaults.ResponseFormat) ? "url" : klingImgDefaults.ResponseFormat,
                Images = klingImgDefaults.Images <= 0 ? 1 : klingImgDefaults.Images,
                NegativePrompt = klingImgDefaults.NegativePrompt
            };
        }

        return config;
    }

    /// <summary>
    /// 合成 Video 配置
    /// </summary>
    private VideoServicesConfiguration ComposeVideoConfiguration()
    {
        var config = new VideoServicesConfiguration();

        if (_defaults?.Video?.Providers.TryGetValue("Qwen", out var qwenDefaults) == true)
        {
            config.Qwen = new QwenVideoConfig
            {
                Resolution = qwenDefaults.Resolution,
                Size = qwenDefaults.Size,
                DurationSeconds = qwenDefaults.DurationSeconds,
                Watermark = qwenDefaults.Watermark,
                PromptExtend = qwenDefaults.PromptExtend,
                ShotType = qwenDefaults.ShotType
            };
        }

        if (_defaults?.Video?.Providers.TryGetValue("Volcengine", out var volcDefaults) == true)
        {
            config.Volcengine = new VolcengineVideoConfig
            {
                Resolution = volcDefaults.Resolution,
                Ratio = volcDefaults.Ratio,
                DurationSeconds = volcDefaults.DurationSeconds,
                Watermark = volcDefaults.Watermark,
                ReturnLastFrame = volcDefaults.ReturnLastFrame,
                ServiceTier = volcDefaults.ServiceTier,
                GenerateAudio = volcDefaults.GenerateAudio,
                Draft = volcDefaults.Draft
            };
        }

        if (_defaults?.Video?.Providers.TryGetValue("NewApi", out var newApiDefaults) == true)
        {
            config.NewApi = new NewApiVideoConfig
            {
                Resolution = newApiDefaults.Resolution,
                Ratio = newApiDefaults.Ratio,
                DurationSeconds = newApiDefaults.DurationSeconds,
                Watermark = newApiDefaults.Watermark,
                ReturnLastFrame = newApiDefaults.ReturnLastFrame,
                ProviderHint = newApiDefaults.ProviderHint
            };
        }

        if (_defaults?.Video?.Providers.TryGetValue("Kling", out var klingVidDefaults) == true)
        {
            config.Kling = new KlingVideoConfig
            {
                AspectRatio = string.IsNullOrWhiteSpace(klingVidDefaults.AspectRatio) ? "16:9" : klingVidDefaults.AspectRatio!,
                DurationSeconds = klingVidDefaults.DurationSeconds <= 0 ? 5 : klingVidDefaults.DurationSeconds,
                Mode = string.IsNullOrWhiteSpace(klingVidDefaults.Mode) ? "std" : klingVidDefaults.Mode!,
                Watermark = klingVidDefaults.Watermark,
                NegativePrompt = klingVidDefaults.NegativePrompt
            };
        }

        return config;
    }

    /// <summary>
    /// 鍚堟垚 TTS 閰嶇疆
    /// </summary>
    private TtsServicesConfiguration ComposeTtsConfiguration(UserAIOverrides userOverrides)
    {
        var config = new TtsServicesConfiguration();

        if (_defaults?.Tts?.Providers.TryGetValue("NewApi", out var newApiDefaults) == true)
        {
            config.NewApi = new NewApiTtsConfig
            {
                Voice = string.IsNullOrWhiteSpace(newApiDefaults.Voice) ? config.NewApi.Voice : newApiDefaults.Voice,
                Speed = newApiDefaults.Speed,
                ResponseFormat = string.IsNullOrWhiteSpace(newApiDefaults.ResponseFormat) ? config.NewApi.ResponseFormat : newApiDefaults.ResponseFormat,
                ProviderHint = string.IsNullOrWhiteSpace(newApiDefaults.ProviderHint) ? config.NewApi.ProviderHint : newApiDefaults.ProviderHint
            };
        }

        if (_defaults?.Tts?.Providers.TryGetValue("Qwen", out var qwenDefaults) == true)
        {
            config.Qwen = new QwenTtsConfig
            {
                Voice = string.IsNullOrWhiteSpace(qwenDefaults.Voice) ? config.Qwen.Voice : qwenDefaults.Voice,
                Speed = qwenDefaults.Speed,
                ResponseFormat = string.IsNullOrWhiteSpace(qwenDefaults.ResponseFormat) ? config.Qwen.ResponseFormat : qwenDefaults.ResponseFormat,
                LanguageType = string.IsNullOrWhiteSpace(qwenDefaults.LanguageType) ? config.Qwen.LanguageType : qwenDefaults.LanguageType
            };
        }

        if (_defaults?.Tts?.Providers.TryGetValue("Volcengine", out var volcDefaults) == true)
        {
            config.Volcengine = new VolcengineTtsConfig
            {
                Voice = string.IsNullOrWhiteSpace(volcDefaults.Voice) ? config.Volcengine.Voice : volcDefaults.Voice,
                Speed = volcDefaults.Speed,
                ResponseFormat = string.IsNullOrWhiteSpace(volcDefaults.ResponseFormat) ? config.Volcengine.ResponseFormat : volcDefaults.ResponseFormat,
                AppId = string.IsNullOrWhiteSpace(volcDefaults.AppId) ? config.Volcengine.AppId : volcDefaults.AppId,
                Cluster = string.IsNullOrWhiteSpace(volcDefaults.Cluster) ? config.Volcengine.Cluster : volcDefaults.Cluster
            };
        }

        if (userOverrides.Providers.TryGetValue("Volcengine", out var volcOverride))
        {
            if (!string.IsNullOrWhiteSpace(volcOverride.AppId))
                config.Volcengine.AppId = volcOverride.AppId.Trim();

            if (!string.IsNullOrWhiteSpace(volcOverride.Cluster))
                config.Volcengine.Cluster = volcOverride.Cluster.Trim();
        }

        return config;
    }

    /// <summary>
    /// 获取 Provider 的默认模型（考虑用户覆盖）
    /// </summary>
    private string? GetProviderDefaultModel(string providerName, string modelType, UserAIOverrides userOverrides)
    {
        // 先查找用户覆盖
        // 回退到系统默认值
        if (_defaults?.Providers.TryGetValue(providerName, out var defaultConfig) == true &&
            defaultConfig.DefaultModels.TryGetValue(modelType, out var defaultModel))
        {
            return defaultModel;
        }

        return null;
    }

    private string ResolveDefaultModel(string? userModel, string providerName, string modelType, UserAIOverrides userOverrides)
    {
        if (!string.IsNullOrWhiteSpace(userModel))
        {
            return userModel.Trim();
        }

        return GetProviderDefaultModel(providerName, modelType, userOverrides) ?? string.Empty;
    }

    /// <summary>
    /// 解析 Provider 类型字符串
    /// </summary>
    private string ResolveProviderName(string? providerName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return fallback;
        }

        if (_schema?.Providers.ContainsKey(providerName) == true)
        {
            return providerName;
        }

        if (_defaults?.Providers.ContainsKey(providerName) == true)
        {
            return providerName;
        }

        return fallback;
    }

    /// <summary>
    /// Parse provider type string.
    /// </summary>
    private AIProviderType ParseProviderType(string providerName)
    {
        return providerName switch
        {
            "Qwen" => AIProviderType.Qwen,
            "Volcengine" => AIProviderType.Volcengine,
            "NewApi" => AIProviderType.NewApi,
            "Kling" => AIProviderType.Kling,
            "DeepSeek" => AIProviderType.DeepSeek,
            _ => AIProviderType.Qwen // 默认值
        };
    }
    private static ImageProviderType MapImageProviderType(AIProviderType providerType)
    {
        return providerType switch
        {
            AIProviderType.Qwen => ImageProviderType.Qwen,
            AIProviderType.Volcengine => ImageProviderType.Volcengine,
            AIProviderType.NewApi => ImageProviderType.NewApi,
            AIProviderType.Kling => ImageProviderType.Kling,
            _ => ImageProviderType.Volcengine
        };
    }

    private static VideoProviderType MapVideoProviderType(AIProviderType providerType)
    {
        return providerType switch
        {
            AIProviderType.Qwen => VideoProviderType.Qwen,
            AIProviderType.Volcengine => VideoProviderType.Volcengine,
            AIProviderType.NewApi => VideoProviderType.NewApi,
            AIProviderType.Kling => VideoProviderType.Kling,
            _ => VideoProviderType.Volcengine
        };
    }

    private static TtsProviderType MapTtsProviderType(AIProviderType providerType)
    {
        return providerType switch
        {
            AIProviderType.Qwen => TtsProviderType.Qwen,
            AIProviderType.Volcengine => TtsProviderType.Volcengine,
            AIProviderType.NewApi => TtsProviderType.NewApi,
            // Kling 暂不提供独立 TTS，回落到 NewApi
            _ => TtsProviderType.NewApi
        };
    }
}
