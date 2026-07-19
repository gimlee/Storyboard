namespace Storyboard.AI.Core;

/// <summary>
/// Shared config base for image/video providers.
/// </summary>
public abstract class AIServiceConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class AIProviderModelDefaults
{
    public string Text { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string Video { get; set; } = string.Empty;
    public string Tts { get; set; } = string.Empty;
}

public sealed class AIProviderConfiguration
{
    public string ApiKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 120;
    public AIProviderModelDefaults DefaultModels { get; set; } = new();
}

public sealed class AIProvidersConfiguration
{
    public AIProviderConfiguration Qwen { get; set; } = new();
    public AIProviderConfiguration Volcengine { get; set; } = new();
    public AIProviderConfiguration NewApi { get; set; } = new();
    public AIProviderConfiguration Kling { get; set; } = new();
    public AIProviderConfiguration DeepSeek { get; set; } = new();
}

public sealed class AIServiceDefaultSelection
{
    public AIProviderType Provider { get; set; } = AIProviderType.Qwen;
    public string Model { get; set; } = string.Empty;
}

public sealed class AIServiceDefaults
{
    public AIServiceDefaultSelection Text { get; set; } = new();
    public AIServiceDefaultSelection Image { get; set; } = new();
    public AIServiceDefaultSelection Video { get; set; } = new();
    public AIServiceDefaultSelection Tts { get; set; } = new();
}

public sealed class AIServicesConfiguration
{
    public AIProvidersConfiguration Providers { get; set; } = new();
    public AIServiceDefaults Defaults { get; set; } = new();

    public ImageServicesConfiguration Image { get; set; } = new();
    public VideoServicesConfiguration Video { get; set; } = new();
    public TtsServicesConfiguration Tts { get; set; } = new();
}

public sealed class VolcengineImageConfig
{
    public string Size { get; set; } = "2K";
    public string ResponseFormat { get; set; } = "b64_json";
    public bool Watermark { get; set; } = false;
    public bool Stream { get; set; } = false;
    public string SequentialImageGeneration { get; set; } = string.Empty;
    public int? SequentialMaxImages { get; set; }
    public string OptimizePromptMode { get; set; } = string.Empty;
}

public sealed class QwenImageConfig
{
    public string Size { get; set; } = "1664*928";
    public string ResponseFormat { get; set; } = "url";
    public bool Watermark { get; set; } = false;
    public bool Stream { get; set; } = false;
    public int Images { get; set; } = 1;
    public bool PromptExtend { get; set; } = true;
}

public class ImageServicesConfiguration
{
    public ImageProviderType DefaultProvider { get; set; } = ImageProviderType.Volcengine;
    public QwenImageConfig Qwen { get; set; } = new();
    public VolcengineImageConfig Volcengine { get; set; } = new();
    public NewApiImageConfig NewApi { get; set; } = new();
    public KlingImageConfig Kling { get; set; } = new();
}

public sealed class VolcengineVideoConfig
{
    public string Resolution { get; set; } = "1080p";
    public string Ratio { get; set; } = string.Empty;
    public double DurationSeconds { get; set; } = 0;
    public int? Frames { get; set; }
    public int? Seed { get; set; }
    public bool? CameraFixed { get; set; }
    public bool Watermark { get; set; } = false;
    public bool ReturnLastFrame { get; set; } = false;
    public string ServiceTier { get; set; } = "default";
    public bool GenerateAudio { get; set; } = false;
    public bool Draft { get; set; } = false;
}

public sealed class QwenVideoConfig
{
    public string Resolution { get; set; } = "720P";
    public string Size { get; set; } = "1280*720";
    public int DurationSeconds { get; set; } = 0;
    public int? Fps { get; set; } = 24;
    public int? Seed { get; set; }
    public bool Watermark { get; set; } = false;
    public bool PromptExtend { get; set; } = true;
    public string ShotType { get; set; } = "single";
}

public class VideoServicesConfiguration
{
    public VideoProviderType DefaultProvider { get; set; } = VideoProviderType.Volcengine;
    public QwenVideoConfig Qwen { get; set; } = new();
    public VolcengineVideoConfig Volcengine { get; set; } = new();
    public NewApiVideoConfig NewApi { get; set; } = new();
    public KlingVideoConfig Kling { get; set; } = new();
}

public sealed class NewApiImageConfig
{
    public string Size { get; set; } = "1024x1024";
    public string ResponseFormat { get; set; } = "b64_json";
    public bool Watermark { get; set; }
    public int Images { get; set; } = 1;
    public bool Stream { get; set; }
    public string? ProviderHint { get; set; }
}

public sealed class NewApiVideoConfig
{
    public string Resolution { get; set; } = "1080p";
    public string Ratio { get; set; } = "16:9";
    public int DurationSeconds { get; set; }
    public bool Watermark { get; set; }
    public bool ReturnLastFrame { get; set; }
    public string? ProviderHint { get; set; }
}

/// <summary>
/// Kling 图像生成配置。
/// </summary>
public sealed class KlingImageConfig
{
    /// <summary>比例，如 16:9 / 1:1 / 9:16 / 4:3 / 3:2 / 2:3</summary>
    public string AspectRatio { get; set; } = "16:9";
    /// <summary>响应格式：url 或 b64_json</summary>
    public string ResponseFormat { get; set; } = "url";
    public int Images { get; set; } = 1;
    public string? NegativePrompt { get; set; }
}

/// <summary>
/// Kling 视频生成配置。
/// </summary>
public sealed class KlingVideoConfig
{
    /// <summary>比例：16:9 / 9:16 / 1:1</summary>
    public string AspectRatio { get; set; } = "16:9";
    /// <summary>时长（秒），Kling 3.0 Turbo 支持 5 / 10 秒</summary>
    public int DurationSeconds { get; set; } = 5;
    /// <summary>模式：std / pro</summary>
    public string Mode { get; set; } = "std";
    public bool Watermark { get; set; }
    public string? NegativePrompt { get; set; }
}

public sealed class NewApiTtsConfig
{
    public string Voice { get; set; } = "alloy";
    public double Speed { get; set; } = 1.0;
    public string ResponseFormat { get; set; } = "mp3";
    public string? ProviderHint { get; set; }
}

public sealed class QwenTtsConfig
{
    public string Voice { get; set; } = "Cherry";
    public double Speed { get; set; } = 1.0;
    public string ResponseFormat { get; set; } = "mp3";
    public string? LanguageType { get; set; } = "Chinese";
}

public sealed class VolcengineTtsConfig
{
    public string Voice { get; set; } = "zh_female_vv_yingjian_soungis"; 
    public double Speed { get; set; } = 1.0;
    public string ResponseFormat { get; set; } = "mp3";
    public string? AppId { get; set; }
    public string Cluster { get; set; } = "volcano_tts";
}

public class TtsServicesConfiguration
{
    public TtsProviderType DefaultProvider { get; set; } = TtsProviderType.NewApi;
    public NewApiTtsConfig NewApi { get; set; } = new();
    public QwenTtsConfig Qwen { get; set; } = new();
    public VolcengineTtsConfig Volcengine { get; set; } = new();
}
