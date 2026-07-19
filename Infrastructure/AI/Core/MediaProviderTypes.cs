namespace Storyboard.AI.Core;

public enum ImageProviderType
{
    Qwen,
    Volcengine,
    NewApi,
    Kling
}

public enum VideoProviderType
{
    Qwen,
    Volcengine,
    NewApi,
    Kling
}

public enum TtsProviderType
{
    Qwen,
    Volcengine,
    NewApi,
    // 注：Kling 当前不提供独立 TTS API，预留枚举成员以保持类型一致性
    Kling
}
