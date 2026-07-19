using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Storyboard.AI.Core;

namespace Storyboard.AI.Providers;

/// <summary>
/// DeepSeek 文本服务 Provider（OpenAI 兼容）。
/// Endpoint 默认 https://api.deepseek.com（已含 /v1 兼容路径），鉴权用普通 Bearer token。
/// 仅支持文本能力。
/// </summary>
public sealed class DeepSeekServiceProvider : BaseAIServiceProvider
{
    private readonly IOptionsMonitor<AIServicesConfiguration> _configMonitor;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DeepSeekServiceProvider(IOptionsMonitor<AIServicesConfiguration> configMonitor, ILogger<DeepSeekServiceProvider> logger)
        : base(logger)
    {
        _configMonitor = configMonitor;
    }

    private AIProviderConfiguration Config => _configMonitor.CurrentValue.Providers.DeepSeek;

    public override AIProviderType ProviderType => AIProviderType.DeepSeek;
    public override string DisplayName => "DeepSeek";

    public override bool IsConfigured =>
        Config.Enabled &&
        !string.IsNullOrWhiteSpace(Config.ApiKey) &&
        !string.IsNullOrWhiteSpace(Config.Endpoint);

    public override IReadOnlyList<string> SupportedModels => Array.Empty<string>();

    public override AIProviderCapability Capabilities => AIProviderCapability.TextUnderstanding;

    public override IReadOnlyList<ProviderCapabilityDeclaration> CapabilityDeclarations => new[]
    {
        new ProviderCapabilityDeclaration(AIProviderCapability.TextUnderstanding, "OpenAI-compatible /v1/chat/completions", "text/plain")
    };

    public override async Task<string> ChatAsync(AIChatRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        EnsureModel(request.Model);

        var payload = BuildRequestPayload(request, stream: false);
        using var httpClient = CreateHttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("chat/completions", content, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("DeepSeek request failed with status {StatusCode}: {Body}", response.StatusCode, responseBody);
            throw new InvalidOperationException($"DeepSeek request failed with status {response.StatusCode}: {responseBody}");
        }

        var trimmedBody = responseBody.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmedBody) || (!trimmedBody.StartsWith("{") && !trimmedBody.StartsWith("[")))
        {
            Logger.LogError("DeepSeek returned non-JSON response. Content-Type: {ContentType}, Body preview: {BodyPreview}",
                response.Content.Headers.ContentType?.ToString() ?? "unknown",
                trimmedBody.Length > 200 ? trimmedBody.Substring(0, 200) : trimmedBody);
            throw new InvalidOperationException($"DeepSeek returned non-JSON response. Check endpoint URL configuration. Response starts with: {(trimmedBody.Length > 50 ? trimmedBody.Substring(0, 50) : trimmedBody)}");
        }

        try
        {
            var result = JsonSerializer.Deserialize<OpenAiResponse>(responseBody, JsonOptions);
            return result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        }
        catch (JsonException ex)
        {
            Logger.LogError(ex, "Failed to deserialize DeepSeek response. Body preview: {BodyPreview}",
                responseBody.Length > 500 ? responseBody.Substring(0, 500) : responseBody);
            throw new InvalidOperationException($"Failed to parse DeepSeek response as JSON.", ex);
        }
    }

    public override async IAsyncEnumerable<string> ChatStreamAsync(
        AIChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        EnsureModel(request.Model);

        var payload = BuildRequestPayload(request, stream: true);
        using var httpClient = CreateHttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", System.StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            var chunk = JsonSerializer.Deserialize<OpenAiResponse>(data, JsonOptions);
            var delta = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }

    public override async Task<bool> ValidateConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            Logger.LogWarning("DeepSeek configuration incomplete.");
            return false;
        }

        try
        {
            var model = ResolveValidationModel();
            var request = new AIChatRequest
            {
                Model = model,
                Messages = new[] { new AIChatMessage(AIChatRole.User, "ping") },
                MaxTokens = 16
            };

            _ = await ChatAsync(request, cancellationToken).ConfigureAwait(false);
            Logger.LogInformation("DeepSeek configuration validated.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "DeepSeek configuration validation failed.");
            return false;
        }
    }

    private string ResolveValidationModel()
    {
        var defaults = _configMonitor.CurrentValue.Defaults.Text;
        if (defaults.Provider == ProviderType && !string.IsNullOrWhiteSpace(defaults.Model))
        {
            return defaults.Model.Trim();
        }

        return string.IsNullOrWhiteSpace(Config.DefaultModels.Text)
            ? "deepseek-chat"
            : Config.DefaultModels.Text.Trim();
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("DeepSeek is not configured.");
        }
    }

    private static void EnsureModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Model is required for DeepSeek requests.");
        }
    }

    private HttpClient CreateHttpClient()
    {
        // DeepSeek 官方 endpoint：https://api.deepseek.com（OpenAI 兼容路径 /v1/chat/completions）。
        // 若用户填的 endpoint 不含 /v1，则补齐。
        var endpoint = Config.Endpoint;
        if (!string.IsNullOrWhiteSpace(endpoint) && !endpoint.Contains("/v1"))
        {
            endpoint = endpoint.TrimEnd('/') + "/v1";
        }
        return CreateHttpClient(endpoint, Config.TimeoutSeconds);
    }

    private static object BuildRequestPayload(AIChatRequest request, bool stream)
    {
        return new
        {
            model = request.Model,
            messages = request.Messages.Select(m => new
            {
                role = MapRole(m.Role),
                content = BuildMessageContent(m)
            }).ToArray(),
            temperature = request.Temperature,
            top_p = request.TopP,
            max_tokens = request.MaxTokens,
            stream
        };
    }

    private static object BuildMessageContent(AIChatMessage message)
    {
        if (message.IsMultimodal && message.MultimodalContent != null)
        {
            var contentArray = new List<object>();
            foreach (var part in message.MultimodalContent)
            {
                if (part.Type == MessageContentType.Text && !string.IsNullOrWhiteSpace(part.Text))
                {
                    contentArray.Add(new { type = "text", text = part.Text });
                }
                else if (part.Type == MessageContentType.ImageBase64 && !string.IsNullOrWhiteSpace(part.ImageBase64))
                {
                    contentArray.Add(new
                    {
                        type = "image_url",
                        image_url = new { url = $"data:image/png;base64,{part.ImageBase64}" }
                    });
                }
                else if (part.Type == MessageContentType.ImageUrl && !string.IsNullOrWhiteSpace(part.ImageUrl))
                {
                    contentArray.Add(new
                    {
                        type = "image_url",
                        image_url = new { url = part.ImageUrl }
                    });
                }
            }

            return contentArray;
        }

        return message.Content ?? string.Empty;
    }

    private static string MapRole(AIChatRole role)
    {
        return role switch
        {
            AIChatRole.System => "system",
            AIChatRole.User => "user",
            AIChatRole.Assistant => "assistant",
            _ => "user"
        };
    }

    private sealed class OpenAiResponse
    {
        public List<OpenAiChoice>? Choices { get; set; }
    }

    private sealed class OpenAiChoice
    {
        public OpenAiMessage? Message { get; set; }
        public OpenAiMessage? Delta { get; set; }
    }

    private sealed class OpenAiMessage
    {
        public string? Content { get; set; }
    }
}
