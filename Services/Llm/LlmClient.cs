using ICOGenerator.Domain;
using ICOGenerator.Services.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ICOGenerator.Services.Llm;

public class LlmClient : ILlmClient
{
    public async Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessageDto> messages, double temperature)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new LlmCallResult
        {
            Endpoint = model.Endpoint,
            ModelId = model.ModelId,
            ModelName = model.Name
        };

        var handler = new HttpClientHandler();

        if (model.Endpoint.Contains("localhost") || model.Endpoint.Contains("127.0.0.1"))
        {
            handler.UseProxy = false;
        }
        else
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy("http://127.0.0.1:3128");
        }

        using var http = new HttpClient(handler);

        http.BaseAddress = new Uri(model.Endpoint.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", string.IsNullOrWhiteSpace(model.ApiKey) ? "lm-studio" : model.ApiKey);

        var request = new ChatCompletionRequestDto
        {
            Model = model.ModelId,
            Messages = messages,
            Temperature = temperature,
            MaxTokens = 100000,
            Stream = true,
            Thinking = new ThinkingDto
            {
                Type = "disabled"
            }
        };

        result.RequestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true });
        result.PromptTokens = TokenEstimator.Estimate(string.Join("\n", messages.Select(x => x.Content)));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        httpRequest.Content = new StringContent(result.RequestJson, Encoding.UTF8, "application/json");

        try
        {
            using var response = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
            result.HttpStatusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await SafeReadErrorAsync(response);
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

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var contentBuilder = new StringBuilder();
            var rawBuilder = new StringBuilder();

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();

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

    private static async Task<string> SafeReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadAsStringAsync();
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
