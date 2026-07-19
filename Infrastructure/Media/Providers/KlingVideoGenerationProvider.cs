using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Storyboard.AI.Core;
using Storyboard.Infrastructure.Media;
using Storyboard.Models;

namespace Storyboard.Infrastructure.Media.Providers;

/// <summary>
/// Kling 视频生成 Provider。
/// 文档：
///   - 文生视频：https://klingai.com/document-api/api/video/3-0-turbo/text-to-video
///   - 图生视频：https://klingai.com/document-api/api/video/3-0-turbo/image-to-video
/// 鉴权：直接使用 API Key 作为 Bearer token（Kling 2026 新版鉴权，无需 JWT）。
/// Kling 是异步任务式：POST 创建任务 → 返回 task_id → 轮询任务状态 → 下载视频。
/// </summary>
public sealed class KlingVideoGenerationProvider : IVideoGenerationProvider
{
    private readonly IOptionsMonitor<AIServicesConfiguration> _configMonitor;
    private readonly ILogger<KlingVideoGenerationProvider> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const int PollIntervalSeconds = 5;
    private const int MaxPollIterations = 120; // ~10 分钟

    public KlingVideoGenerationProvider(
        IOptionsMonitor<AIServicesConfiguration> configMonitor,
        ILogger<KlingVideoGenerationProvider> logger)
    {
        _configMonitor = configMonitor;
        _logger = logger;
    }

    private AIProviderConfiguration ProviderConfig => _configMonitor.CurrentValue.Providers.Kling;
    private KlingVideoConfig VideoConfig => _configMonitor.CurrentValue.Video.Kling;

    public VideoProviderType ProviderType => VideoProviderType.Kling;
    public string DisplayName => "Kling";

    public bool IsConfigured =>
        ProviderConfig.Enabled &&
        !string.IsNullOrWhiteSpace(ProviderConfig.ApiKey) &&
        !string.IsNullOrWhiteSpace(ProviderConfig.Endpoint);

    public IReadOnlyList<string> SupportedModels => new[]
    {
        "kling-v3",
        "kling-v2-master",
        "kling-v1"
    };

    public IReadOnlyList<ProviderCapabilityDeclaration> CapabilityDeclarations => new[]
    {
        new ProviderCapabilityDeclaration(AIProviderCapability.VideoGeneration, "Kling /v1/videos/text2video, /v1/videos/image2video", "video/mp4")
    };

    public async Task GenerateAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Kling video generation is not configured.");

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? ProviderConfig.DefaultModels.Video
            : request.Model;
        if (string.IsNullOrWhiteSpace(model))
            model = "kling-v3";

        var shot = request.Shot;
        var prompt = BuildPrompt(shot);
        var duration = ResolveDurationSeconds(shot);
        var aspectRatio = !string.IsNullOrWhiteSpace(VideoConfig.AspectRatio) ? VideoConfig.AspectRatio : "16:9";
        var negativePrompt = !string.IsNullOrWhiteSpace(shot.VideoNegativePrompt)
            ? shot.VideoNegativePrompt.Trim()
            : VideoConfig.NegativePrompt?.Trim();

        using var httpClient = CreateHttpClient();

        var referenceImage = TryLoadReferenceImage(shot);
        var isImage2Video = referenceImage.HasValue;

        string createPath = isImage2Video ? "v1/videos/image2video" : "v1/videos/text2video";
        var payload = new Dictionary<string, object?>
        {
            ["model_name"] = model,
            ["prompt"] = prompt,
            ["duration"] = duration.ToString(CultureInfo.InvariantCulture),
            ["aspect_ratio"] = aspectRatio,
            ["mode"] = string.IsNullOrWhiteSpace(VideoConfig.Mode) ? "std" : VideoConfig.Mode
        };

        if (!string.IsNullOrWhiteSpace(negativePrompt))
            payload["negative_prompt"] = negativePrompt;

        if (isImage2Video)
        {
            var (imageType, b64) = referenceImage.Value;
            payload["image"] = b64;
            payload["image_type"] = imageType;
        }

        _logger.LogInformation(
            "Kling video generation: Mode={Mode}({Sub}), Model={Model}, Duration={Dur}s, AspectRatio={Ratio}, HasRef={Ref}",
            VideoConfig.Mode, isImage2Video ? "i2v" : "t2v", model, duration, aspectRatio, isImage2Video);

        var createContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var createResponse = await httpClient.PostAsync(createPath, createContent, cancellationToken).ConfigureAwait(false);
        var createBody = await createResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!createResponse.IsSuccessStatusCode)
        {
            _logger.LogError("Kling video create failed with status {StatusCode}: {Body}", createResponse.StatusCode, createBody);
            throw new InvalidOperationException($"Kling video generation failed with status {createResponse.StatusCode}: {createBody}");
        }

        KlingTaskResponse createResult;
        try
        {
            createResult = JsonSerializer.Deserialize<KlingTaskResponse>(createBody, JsonOptions)
                           ?? throw new InvalidOperationException("Unable to parse Kling video create response.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Kling create response. Body preview: {BodyPreview}",
                createBody.Length > 500 ? createBody.Substring(0, 500) : createBody);
            throw new InvalidOperationException("Failed to parse Kling create response as JSON.", ex);
        }

        // Kling 在 data.task_id 返回任务 id
        var taskId = createResult.Data?.TaskId;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new InvalidOperationException(
                $"Kling video generation did not return a task id. Response: {createBody}");
        }

        _logger.LogInformation("Kling task created: {TaskId}", taskId);

        // 轮询查询任务
        var queryPath = isImage2Video ? $"v1/videos/image2video/{taskId}" : $"v1/videos/text2video/{taskId}";
        var finalStatus = await PollTaskStatusAsync(httpClient, queryPath, taskId, cancellationToken).ConfigureAwait(false);

        var videoUrl = finalStatus.Data?.TaskResult?.Videos?.FirstOrDefault()?.Url;
        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            throw new InvalidOperationException($"Kling video generation completed but no video URL found. Task: {taskId}");
        }

        await DownloadAsync(httpClient, videoUrl, request.OutputPath, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Kling video saved to {Path}", request.OutputPath);
    }

    private HttpClient CreateHttpClient()
    {
        var baseAddress = ProviderConfig.Endpoint?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseAddress))
            throw new InvalidOperationException("Endpoint is required for Kling video generation.");

        var client = new HttpClient
        {
            BaseAddress = new Uri($"{baseAddress}/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(60, ProviderConfig.TimeoutSeconds))
        };

        // Kling 新版鉴权：直接用 API Key 作为 Bearer token
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ProviderConfig.ApiKey);
        return client;
    }

    private async Task<KlingTaskResponse> PollTaskStatusAsync(
        HttpClient httpClient,
        string queryPath,
        string taskId,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < MaxPollIterations; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), cancellationToken).ConfigureAwait(false);

            var response = await httpClient.GetAsync(queryPath, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Kling video polling failed: {body}");
            }

            var status = JsonSerializer.Deserialize<KlingTaskResponse>(body, JsonOptions)
                         ?? throw new InvalidOperationException("Unable to parse Kling polling response.");

            var taskStatus = status.Data?.TaskStatus;
            _logger.LogDebug("Kling task {TaskId} status: {Status}", taskId, taskStatus);

            if (IsSucceeded(taskStatus))
            {
                return status;
            }

            if (IsFailed(taskStatus))
            {
                var videoCode = status.Data?.TaskResult?.Videos?.FirstOrDefault()?.Code;
                var detail = videoCode.HasValue
                    ? videoCode.Value.ToString()
                    : (status.Code?.ToString() ?? "unknown");
                var msg = status.Data?.TaskStatusMsg;
                throw new InvalidOperationException(
                    $"Kling video generation failed (status={taskStatus}, detail={detail}{(string.IsNullOrWhiteSpace(msg) ? "" : $", msg={msg}")}).");
            }
        }

        throw new TimeoutException("Kling video generation polling timed out.");
    }

    private static async Task DownloadAsync(
        HttpClient httpClient,
        string url,
        string outputPath,
        CancellationToken cancellationToken)
    {
        // Kling 的视频 URL 可能是绝对地址；用独立的 HttpClient 走 GET（避免 BaseAddress 限制）
        using var download = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        download.EnsureSuccessStatusCode();

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var fileStream = File.Create(outputPath);
        await download.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildPrompt(ShotItem shot)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(shot.VideoPrompt))
            builder.AppendLine(shot.VideoPrompt);

        if (!string.IsNullOrWhiteSpace(shot.SceneDescription))
            builder.AppendLine($"场景: {shot.SceneDescription}");
        if (!string.IsNullOrWhiteSpace(shot.ActionDescription))
            builder.AppendLine($"动作: {shot.ActionDescription}");
        if (!string.IsNullOrWhiteSpace(shot.StyleDescription))
            builder.AppendLine($"风格: {shot.StyleDescription}");
        if (!string.IsNullOrWhiteSpace(shot.CoreContent))
            builder.AppendLine($"核心: {shot.CoreContent}");
        if (!string.IsNullOrWhiteSpace(shot.CameraMovement))
            builder.AppendLine($"镜头运动: {shot.CameraMovement}");
        if (!string.IsNullOrWhiteSpace(shot.ShootingStyle))
            builder.AppendLine($"拍摄手法: {shot.ShootingStyle}");
        if (!string.IsNullOrWhiteSpace(shot.VideoEffect))
            builder.AppendLine($"视觉效果: {shot.VideoEffect}");

        var prompt = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(prompt) ? "Create a cinematic storyboard shot." : prompt;
    }

    private int ResolveDurationSeconds(ShotItem shot)
    {
        var duration = (int)Math.Round(shot.EffectiveGeneratedDurationSeconds);
        if (duration <= 0)
            duration = VideoConfig.DurationSeconds > 0 ? VideoConfig.DurationSeconds : 5;
        // Kling 3.0 Turbo 支持 5 / 10 秒，按近似取整
        if (duration <= 5)
            duration = 5;
        else
            duration = 10;

        if (VideoConfig.DurationSeconds > 0)
        {
            duration = Math.Min(duration, VideoConfig.DurationSeconds);
        }

        return duration;
    }

    /// <summary>
    /// 加载参考图（base64 + 类型）。Kling 仅支持单图，取首帧优先。
    /// </summary>
    private static (string ImageType, string Base64)? TryLoadReferenceImage(ShotItem shot)
    {
        string? path = null;

        if (!string.IsNullOrWhiteSpace(shot.FirstFrameImagePath))
        {
            path = shot.FirstFrameImagePath;
        }
        else if (!string.IsNullOrWhiteSpace(shot.LastFrameImagePath))
        {
            path = shot.LastFrameImagePath;
        }
        else if (shot.UseFirstFrameReference && shot.FirstFrameAssets.Count > 0)
        {
            var firstAsset = shot.FirstFrameAssets.FirstOrDefault();
            if (firstAsset != null && !string.IsNullOrWhiteSpace(firstAsset.FilePath))
                path = firstAsset.FilePath;
        }
        else if (shot.UseLastFrameReference && shot.LastFrameAssets.Count > 0)
        {
            var lastAsset = shot.LastFrameAssets.FirstOrDefault();
            if (lastAsset != null && !string.IsNullOrWhiteSpace(lastAsset.FilePath))
                path = lastAsset.FilePath;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var bytes = File.ReadAllBytes(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var imageType = ext == ".jpg" || ext == ".jpeg" ? "jpg" : "png";
        return (imageType, Convert.ToBase64String(bytes));
    }

    private static bool IsSucceeded(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;
        return status.Equals("succeed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("success", StringComparison.OrdinalIgnoreCase)
               || status == "100";
    }

    private static bool IsFailed(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;
        var lower = status.ToLowerInvariant();
        return lower.Contains("fail") || lower.Contains("error");
    }

    private sealed class KlingTaskResponse
    {
        [JsonPropertyName("code")]
        public int? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("data")]
        public KlingTaskData? Data { get; set; }
    }

    private sealed class KlingTaskData
    {
        [JsonPropertyName("task_id")]
        public string? TaskId { get; set; }

        [JsonPropertyName("task_status")]
        public string? TaskStatus { get; set; }

        [JsonPropertyName("task_status_msg")]
        public string? TaskStatusMsg { get; set; }

        [JsonPropertyName("task_result")]
        public KlingTaskResult? TaskResult { get; set; }
    }

    private sealed class KlingTaskResult
    {
        [JsonPropertyName("videos")]
        public List<KlingVideo>? Videos { get; set; }
    }

    private sealed class KlingVideo
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("duration")]
        public string? Duration { get; set; }

        /// <summary>失败时的错误码。</summary>
        [JsonPropertyName("code")]
        public int? Code { get; set; }
    }
}
