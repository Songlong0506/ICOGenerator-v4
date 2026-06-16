using ICOGenerator.Domain;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ICOGenerator.Services.Llm;

public class LlmClient : ILlmClient
{
    // Named clients registered in DI; the factory pools the underlying handlers so
    // we no longer allocate a fresh HttpClientHandler/HttpClient per call (which
    // risks socket exhaustion). Proxy choice is baked into the handler per name.
    public const string DirectClientName = "llm-direct";
    public const string ProxiedClientName = "llm-proxied";

    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

    // Overall ceiling for a single LLM call. Because we stream with
    // ResponseHeadersRead, HttpClient.Timeout only bounds time-to-headers, not the
    // body read loop — without this a model that stalls mid-stream would hang the
    // single background worker forever. Configurable via Llm:RequestTimeoutSeconds.
    private const int DefaultRequestTimeoutSeconds = 600;

    // Upper bound for completion tokens, and the headroom reserved for the prompt so a
    // model with a small context window isn't asked for more output than it can fit.
    private const int MaxCompletionTokens = 100000;
    private const int ContextSafetyMargin = 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly int _requestTimeoutSeconds;

    public LlmClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
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

        var isLocal = model.Endpoint.Contains("localhost") || model.Endpoint.Contains("127.0.0.1");
        var http = _httpClientFactory.CreateClient(isLocal ? DirectClientName : ProxiedClientName);

        http.BaseAddress = new Uri(model.Endpoint.TrimEnd('/') + "/");
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

        // Link the caller's token (e.g. app shutdown) with an overall request deadline
        // so both an external cancel and a stalled stream unwind the call.
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

                    if (!root.TryGetProperty("choices", out var choices))
                        continue;

                    if (choices.GetArrayLength() == 0)
                        continue;

                    var choice = choices[0];

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

            result.Content = contentBuilder.ToString();
            result.ExtractedContent = result.Content;
            result.ResponseText = rawBuilder.Length > 0 ? rawBuilder.ToString() : result.Content;
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.IsSuccess = true;
            result.CompletionTokens = TokenEstimator.Estimate(result.Content);
            result.TotalTokens = result.PromptTokens + result.CompletionTokens;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller (e.g. app shutdown) cancelled — let it propagate so the
            // background worker treats it as a clean stop rather than a failed call.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own deadline fired: a stalled/too-slow stream. Surface it as a
            // normal failed result so callers can report it instead of hanging.
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

            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.IsSuccess = false;
            result.ErrorMessage = ex.ToString();
            result.Content = ex.Message;
            result.ResponseText = ex.ToString();
            result.CompletionTokens = TokenEstimator.Estimate(result.Content);
            result.TotalTokens = result.PromptTokens + result.CompletionTokens;
            return result;
        }
    }

    // Cap completion tokens to what remains in the model's context window after the
    // (estimated) prompt instead of a fixed 100k, which overflows small-context models
    // and gets rejected by the API. Falls back to the cap when the window is unknown.
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
