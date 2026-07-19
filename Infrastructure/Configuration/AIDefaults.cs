using System.Collections.Generic;

namespace Storyboard.Infrastructure.Configuration;

/// <summary>
/// AI defaults maintained by the application.
/// </summary>
public class AIDefaults
{
    public Dictionary<string, ProviderDefaultConfig> Providers { get; set; } = new();
    public ImageGenerationDefaults Image { get; set; } = new();
    public VideoGenerationDefaults Video { get; set; } = new();
    public TtsGenerationDefaults Tts { get; set; } = new();
}

/// <summary>
/// Provider defaults.
/// </summary>
public class ProviderDefaultConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 120;
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> DefaultModels { get; set; } = new();
}

/// <summary>
/// Image generation defaults.
/// </summary>
public class ImageGenerationDefaults
{
    public Dictionary<string, ImageProviderDefaults> Providers { get; set; } = new();
}

public class ImageProviderDefaults
{
    public string Size { get; set; } = "2K";
    public string ResponseFormat { get; set; } = "b64_json";
    public bool Watermark { get; set; } = false;
    public bool Stream { get; set; } = false;
    public int Images { get; set; } = 1;
    public bool PromptExtend { get; set; } = true;
    public string? ProviderHint { get; set; } = string.Empty;
    /// <summary>Kling 比例（如 16:9）。其他 provider 忽略。</summary>
    public string? AspectRatio { get; set; }
    /// <summary>Kling 反向提示词。其他 provider 忽略。</summary>
    public string? NegativePrompt { get; set; }
}

/// <summary>
/// Video generation defaults.
/// </summary>
public class VideoGenerationDefaults
{
    public Dictionary<string, VideoProviderDefaults> Providers { get; set; } = new();
}

public class VideoProviderDefaults
{
    public string Resolution { get; set; } = "1080p";
    public string Size { get; set; } = string.Empty;
    public string Ratio { get; set; } = string.Empty;
    public int DurationSeconds { get; set; } = 0;
    public bool Watermark { get; set; } = false;
    public bool ReturnLastFrame { get; set; } = false;
    public string ServiceTier { get; set; } = "default";
    public bool GenerateAudio { get; set; } = false;
    public bool Draft { get; set; } = false;
    public bool PromptExtend { get; set; } = true;
    public string ShotType { get; set; } = "single";
    public string? ProviderHint { get; set; } = string.Empty;
    /// <summary>Kling 比例（16:9 / 9:16 / 1:1）。其他 provider 忽略。</summary>
    public string? AspectRatio { get; set; }
    /// <summary>Kling 模式（std / pro）。其他 provider 忽略。</summary>
    public string? Mode { get; set; } = "std";
    /// <summary>Kling 反向提示词。其他 provider 忽略。</summary>
    public string? NegativePrompt { get; set; }
}

/// <summary>
/// TTS defaults.
/// </summary>
public class TtsGenerationDefaults
{
    public string DefaultProvider { get; set; } = "NewApi";
    public Dictionary<string, TtsProviderDefaults> Providers { get; set; } = new();
}

public class TtsProviderDefaults
{
    public string Voice { get; set; } = string.Empty;
    public double Speed { get; set; } = 1.0;
    public string ResponseFormat { get; set; } = "mp3";
    public string? ProviderHint { get; set; } = string.Empty;
    public string? LanguageType { get; set; } = string.Empty;
    public string? AppId { get; set; } = string.Empty;
    public string Cluster { get; set; } = "volcano_tts";
}
