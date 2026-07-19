using System;
using System.Collections.Generic;
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

namespace Storyboard.Infrastructure.Media.Providers;

/// <summary>
/// Kling 图片生成 Provider（异步任务模式）。
/// 文档：https://klingai.com/document-api/api/image/3-0-omni/image-generation
/// 鉴权：直接使用 API Key 作为 Bearer token（Kling 2026 新版鉴权）。
/// 流程：POST /v1/images/generations 创建任务 → 轮询 GET /v1/images/generations/{task_id} → 下载图片。
/// </summary>
public sealed class KlingImageGenerationProvider : IImageGenerationProvider
{
    private readonly IOptionsMonitor<AIServicesConfiguration> _configMonitor;
    private readonly ILogger<KlingImageGenerationProvider> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const int PollIntervalSeconds = 3;
    private const int MaxPollIterations = 60; // ~3 分钟

    public KlingImageGenerationProvider(
        IOptionsMonitor<AIServicesConfiguration> configMonitor,
        ILogger<KlingImageGenerationProvider> logger)
    {
        _configMonitor = configMonitor;
        _logger = logger;
    }

    private AIProviderConfiguration ProviderConfig => _configMonitor.CurrentValue.Providers.Kling;
    private KlingImageConfig ImageConfig => _configMonitor.CurrentValue.Image.Kling;

    public ImageProviderType ProviderType => ImageProviderType.Kling;
    public string DisplayName => "Kling";

    public bool IsConfigured =>
        ProviderConfig.Enabled &&
        !string.IsNullOrWhiteSpace(ProviderConfig.ApiKey) &&
        !string.IsNullOrWhiteSpace(ProviderConfig.Endpoint);

    public IReadOnlyList<string> SupportedModels => new[]
    {
        "kling-v3",
        "kling-v2",
        "kling-v1-5",
        "kling-v1"
    };

    public IReadOnlyList<ProviderCapabilityDeclaration> CapabilityDeclarations => new[]
    {
        new ProviderCapabilityDeclaration(AIProviderCapability.ImageGeneration, "Kling /v1/images/generations", "image/png")
    };

    public async Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Kling image generation is not configured.");
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new InvalidOperationException("Image prompt is empty.");

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? ProviderConfig.DefaultModels.Image
            : request.Model;
        if (string.IsNullOrWhiteSpace(model))
            model = "kling-v3";

        var aspectRatio = ResolveAspectRatio(request);
        var n = Math.Clamp(request.MaxImages ?? ImageConfig.Images, 1, 4);
        var negativePrompt = !string.IsNullOrWhiteSpace(request.NegativePrompt)
            ? request.NegativePrompt.Trim()
            : ImageConfig.NegativePrompt?.Trim();
        var prompt = BuildPrompt(request);

        var payload = new Dictionary<string, object?>
        {
            ["model_name"] = model,
            ["prompt"] = prompt,
            ["aspect_ratio"] = aspectRatio,
            ["n"] = n
        };

        if (!string.IsNullOrWhiteSpace(negativePrompt))
            payload["negative_prompt"] = negativePrompt;

        // 参考图（图生图）：Kling 支持 image + image_type（base64）
        var references = BuildReferenceImages(request.ReferenceImagePaths);
        if (references.Count > 0)
        {
            payload["image"] = references[0].Data;
            payload["image_type"] = references[0].Type;
        }

        using var httpClient = CreateHttpClient();

        _logger.LogInformation(
            "Kling image generation: Model={Model}, AspectRatio={Ratio}, N={N}, RefCount={Ref}, PromptLen={Len}",
            model, aspectRatio, n, references.Count, prompt.Length);

        // 1. 创建任务
        var createContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var createResponse = await httpClient.PostAsync("v1/images/generations", createContent, cancellationToken)
            .ConfigureAwait(false);
        var createBody = await createResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!createResponse.IsSuccessStatusCode)
        {
            _logger.LogError("Kling image create failed with status {StatusCode}: {Body}", createResponse.StatusCode, createBody);
            throw new InvalidOperationException($"Kling image generation failed with status {createResponse.StatusCode}: {createBody}");
        }

        KlingTaskResponse createResult;
        try
        {
            createResult = JsonSerializer.Deserialize<KlingTaskResponse>(createBody, JsonOptions)
                           ?? throw new InvalidOperationException("Unable to parse Kling image create response.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Kling create response. Body preview: {BodyPreview}",
                createBody.Length > 500 ? createBody.Substring(0, 500) : createBody);
            throw new InvalidOperationException("Failed to parse Kling image create response as JSON.", ex);
        }

        var taskId = createResult.Data?.TaskId;
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException($"Kling image generation did not return a task id. Response: {createBody}");

        _logger.LogInformation("Kling image task created: {TaskId}", taskId);

        // 2. 轮询查询任务
        var finalStatus = await PollTaskStatusAsync(httpClient, $"v1/images/generations/{taskId}", taskId, cancellationToken)
            .ConfigureAwait(false);

        // 3. 下载图片
        var imageUrl = finalStatus.Data?.TaskResult?.Images?.FirstOrDefault()?.Url;
        if (string.IsNullOrWhiteSpace(imageUrl))
            throw new InvalidOperationException($"Kling image generation completed but no image URL found. Task: {taskId}");

        var download = await httpClient.GetAsync(imageUrl, cancellationToken).ConfigureAwait(false);
        download.EnsureSuccessStatusCode();
        var imageBytes = await download.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var extension = ResolveExtension(download.Content.Headers.ContentType?.MediaType);

        return new ImageGenerationResult(imageBytes, extension, model);
    }

    private HttpClient CreateHttpClient()
    {
        var baseAddress = ProviderConfig.Endpoint?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseAddress))
            throw new InvalidOperationException("Endpoint is required for Kling image generation.");

        var client = new HttpClient
        {
            BaseAddress = new Uri($"{baseAddress}/"),
            Timeout = TimeSpan.FromSeconds(ProviderConfig.TimeoutSeconds)
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
                throw new InvalidOperationException($"Kling image polling failed: {body}");

            var status = JsonSerializer.Deserialize<KlingTaskResponse>(body, JsonOptions)
                         ?? throw new InvalidOperationException("Unable to parse Kling polling response.");

            var taskStatus = status.Data?.TaskStatus;
            _logger.LogDebug("Kling image task {TaskId} status: {Status}", taskId, taskStatus);

            if (IsSucceeded(taskStatus))
                return status;

            if (IsFailed(taskStatus))
            {
                var msg = status.Data?.TaskStatusMsg;
                throw new InvalidOperationException(
                    $"Kling image generation failed (status={taskStatus}{(string.IsNullOrWhiteSpace(msg) ? "" : $", msg={msg}")}).");
            }
        }

        throw new TimeoutException("Kling image generation polling timed out.");
    }

    private string ResolveAspectRatio(ImageGenerationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            return request.AspectRatio.Trim();
        if (!string.IsNullOrWhiteSpace(ImageConfig.AspectRatio))
            return ImageConfig.AspectRatio;
        // 根据 Width/Height 推断
        if (request.Width > 0 && request.Height > 0)
        {
            var (w, h) = ReduceRatio(request.Width, request.Height);
            return $"{w}:{h}";
        }
        return "16:9";
    }

    private static string BuildPrompt(ImageGenerationRequest request)
    {
        var parts = new List<string> { request.Prompt };
        if (!string.IsNullOrWhiteSpace(request.Style))
            parts.Add($"风格: {request.Style}");
        if (!string.IsNullOrWhiteSpace(request.ShotType))
            parts.Add($"景别: {request.ShotType}");
        if (!string.IsNullOrWhiteSpace(request.Composition))
            parts.Add($"构图: {request.Composition}");
        if (!string.IsNullOrWhiteSpace(request.LightingType))
            parts.Add($"光线: {request.LightingType}");
        if (!string.IsNullOrWhiteSpace(request.TimeOfDay))
            parts.Add($"时间: {request.TimeOfDay}");
        if (!string.IsNullOrWhiteSpace(request.ColorStyle))
            parts.Add($"色调: {request.ColorStyle}");

        return string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static List<ReferenceImage> BuildReferenceImages(List<string>? imagePaths)
    {
        var result = new List<ReferenceImage>();
        if (imagePaths == null || imagePaths.Count == 0)
            return result;

        foreach (var path in imagePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;

            var bytes = File.ReadAllBytes(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var imageType = ext == ".jpg" || ext == ".jpeg" ? "jpg" : "png";
            result.Add(new ReferenceImage(imageType, Convert.ToBase64String(bytes)));
            break; // Kling 单图参考
        }

        return result;
    }

    private static string ResolveExtension(string? mediaType)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".png"
        };
    }

    /// <summary>把 width/height 化简为近似的小整数比例（如 16:9）。</summary>
    private static (int W, int H) ReduceRatio(int w, int h)
    {
        var g = Gcd(w, h);
        return (w / g, h / g);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            var t = b;
            b = a % b;
            a = t;
        }
        return a == 0 ? 1 : a;
    }

    private static bool IsSucceeded(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;
        return status.Equals("succeed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("success", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFailed(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;
        var lower = status.ToLowerInvariant();
        return lower.Contains("fail") || lower.Contains("error");
    }

    private sealed record ReferenceImage(string Type, string Data);

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
        [JsonPropertyName("images")]
        public List<KlingImage>? Images { get; set; }
    }

    private sealed class KlingImage
    {
        [JsonPropertyName("index")]
        public int? Index { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
