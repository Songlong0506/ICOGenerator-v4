using ICOGenerator.Domain;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ICOGenerator.Services.Llm;

public class LlmClient : ILlmClient
{
    // DI-registered named clients; the factory pools handlers (avoiding per-call socket
    // exhaustion) and bakes the proxy choice into each handler.
    public const string DirectClientName = "llm-direct";
    public const string ProxiedClientName = "llm-proxied";

    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

    // Overall per-call deadline, enforced via a linked CancellationToken in ChatWithLogAsync
    // (HttpClient's own Timeout is disabled there). Configurable via Llm:RequestTimeoutSeconds.
    private const int DefaultRequestTimeoutSeconds = 600;

    // Upper bound for completion tokens, plus prompt headroom so small-context models aren't
    // asked for more output than fits.
    private const int MaxCompletionTokens = 100000;
    private const int ContextSafetyMargin = 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlmClient> _logger;
    private readonly int _requestTimeoutSeconds;

    public LlmClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<LlmClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _requestTimeoutSeconds = configuration.GetValue("Llm:RequestTimeoutSeconds", DefaultRequestTimeoutSeconds);
    }

    public async Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessageDto> messages, double temperature, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new LlmCallResult
        {
            Endpoint = model.Endpoint,
            ModelId = model.ModelId,
            ModelName = model.Name
        };

        var isLocal = model.Endpoint.Contains("localhost")
            || model.Endpoint.Contains("127.0.0.1")
            || model.Endpoint.Contains("::1"); // IPv6 loopback, incl. the [::1] URL form
        var http = _httpClientFactory.CreateClient(isLocal ? DirectClientName : ProxiedClientName);

        http.BaseAddress = new Uri(model.Endpoint.TrimEnd('/') + "/");
        // Disable HttpClient's 100s default; with ResponseHeadersRead it would cap time-to-headers
        // and override Llm:RequestTimeoutSeconds. The linked timeoutCts below is the sole deadline.
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", string.IsNullOrWhiteSpace(model.ApiKey) ? "lm-studio" : model.ApiKey);

        result.PromptTokens = TokenEstimator.Estimate(string.Join("\n", messages.Select(x => x.Content)));

        var request = new ChatCompletionRequestDto
        {
            Model = model.ModelId,
            Messages = messages,
            Temperature = temperature,
            MaxTokens = ResolveMaxTokens(model, result.PromptTokens),
            Stream = true,
            Thinking = new ThinkingDto
            {
                Type = "disabled"
            }
        };

        result.RequestJson = JsonSerializer.Serialize(request, SerializeOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        httpRequest.Content = new StringContent(result.RequestJson, Encoding.UTF8, "application/json");

        // Link the caller's token (e.g. app shutdown) with the request deadline so either can unwind the call.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_requestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var token = linkedCts.Token;

        try
        {
            using var response = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token);
            result.HttpStatusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await SafeReadErrorAsync(response, token);
                stopwatch.Stop();

                result.DurationMs = stopwatch.ElapsedMilliseconds;
                result.IsSuccess = false;
                result.ResponseText = errorText;
                result.ErrorMessage = $"API error: {(int)response.StatusCode} {response.StatusCode}";
                result.Content = $"""
API error: {(int)response.StatusCode} {response.StatusCode}

{errorText}
""";
                result.CompletionTokens = TokenEstimator.Estimate(result.Content);
                result.TotalTokens = result.PromptTokens + result.CompletionTokens;
                return result;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream);

            var contentBuilder = new StringBuilder();
            var rawBuilder = new StringBuilder();
            string? streamError = null;
            string? finishReason = null;

            string? line;
            while ((line = await reader.ReadLineAsync(token)) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                rawBuilder.AppendLine(line);

                if (line.StartsWith(":"))
                    continue;

                if (!line.StartsWith("data:"))
                    continue;

                var data = line.Substring("data:".Length).Trim();

                if (data == "[DONE]")
                    break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    // Some OpenAI-compatible servers stream an error object with HTTP 200; without
                    // this it would be reported as a successful (empty) completion.
                    if (root.TryGetProperty("error", out var errorElement))
                    {
                        streamError = errorElement.TryGetProperty("message", out var errorMessage)
                            && errorMessage.ValueKind == JsonValueKind.String
                                ? errorMessage.GetString()
                                : errorElement.ToString();
                        break;
                    }

                    if (!root.TryGetProperty("choices", out var choices))
                        continue;

                    if (choices.GetArrayLength() == 0)
                        continue;

                    var choice = choices[0];

                    if (choice.TryGetProperty("finish_reason", out var finishElement)
                        && finishElement.ValueKind == JsonValueKind.String)
                    {
                        finishReason = finishElement.GetString();
                    }

                    if (!choice.TryGetProperty("delta", out var delta))
                        continue;

                    if (delta.TryGetProperty("content", out var contentElement))
                    {
                        var content = contentElement.GetString();

                        if (!string.IsNullOrEmpty(content))
                            contentBuilder.Append(content);
                    }
                }
                catch
                {
                    // Ignore broken SSE chunk
                }
            }

            stopwatch.Stop();

            // A mid-stream error frame means the call failed despite HTTP 200; report failure, not empty success.
            if (streamError != null)
            {
                result.DurationMs = stopwatch.ElapsedMilliseconds;
                result.IsSuccess = false;
                result.ErrorMessage = $"LLM stream error: {streamError}";
                result.Content = result.ErrorMessage;
                result.ResponseText = rawBuilder.Length > 0 ? rawBuilder.ToString() : result.ErrorMessage;
                result.CompletionTokens = TokenEstimator.Estimate(result.Content);
                result.TotalTokens = result.PromptTokens + result.CompletionTokens;
                return result;
            }

            result.Content = contentBuilder.ToString();
            result.ExtractedContent = result.Content;
            result.ResponseText = rawBuilder.Length > 0 ? rawBuilder.ToString() : result.Content;
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.IsSuccess = true;
            // finish_reason == "length" means the model hit its token cap mid-output (often
            // truncated JSON); flag it so a cut-off answer is distinguishable from a clean one.
            if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                result.ErrorMessage = "Phản hồi có thể bị cắt do đạt giới hạn token (finish_reason=length).";
            result.CompletionTokens = TokenEstimator.Estimate(result.Content);
            result.TotalTokens = result.PromptTokens + result.CompletionTokens;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller (e.g. app shutdown) cancelled — propagate so the worker treats it as a clean stop.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own deadline fired (stalled/slow stream): return a failed result instead of hanging.
            stopwatch.Stop();

            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.IsSuccess = false;
            result.ErrorMessage = $"LLM request timed out after {_requestTimeoutSeconds}s.";
            result.Content = result.ErrorMessage;
            result.ResponseText = result.ErrorMessage;
            result.CompletionTokens = TokenEstimator.Estimate(result.Content);
            result.TotalTokens = result.PromptTokens + result.CompletionTokens;
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Log the full exception, but keep only the short message in DB-persisted, UI-visible
            // fields so internal stack/paths aren't leaked.
            _logger.LogError(ex, "LLM call to {Endpoint} ({ModelId}) failed.", model.Endpoint, model.ModelId);

            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.IsSuccess = false;
            result.ErrorMessage = ex.Message;
            result.Content = ex.Message;
            result.ResponseText = ex.Message;
            result.CompletionTokens = TokenEstimator.Estimate(result.Content);
            result.TotalTokens = result.PromptTokens + result.CompletionTokens;
            return result;
        }
    }

    // Cap completion tokens to what's left in the context window after the (estimated) prompt;
    // a fixed 100k overflows small-context models. Falls back to the cap when the window is unknown.
    private static int ResolveMaxTokens(AiModel model, int promptTokens)
    {
        if (model.ContextWindow <= 0)
            return MaxCompletionTokens;

        var available = model.ContextWindow - promptTokens - ContextSafetyMargin;
        return Math.Clamp(available, 256, MaxCompletionTokens);
    }

    private static async Task<string> SafeReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Cannot read error body: {ex.Message}";
        }
    }
}

public class LlmCallResult
{
    public string Content { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public string? ExtractedContent { get; set; }
    public string? ErrorMessage { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public long DurationMs { get; set; }
    public int? HttpStatusCode { get; set; }
    public bool IsSuccess { get; set; }
}
