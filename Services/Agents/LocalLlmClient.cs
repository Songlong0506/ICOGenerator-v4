using ICOGenerator.Domain;
using ICOGenerator.Services.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ICOGenerator.Services.Agents;

public class LocalLlmClient
{
    public async Task<string> ChatAsync(AiModel model, List<ChatMessageDto> messages, double temperature)
    {
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
        var json = JsonSerializer.Serialize(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");

        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(httpRequest,HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await SafeReadErrorAsync(response);
            return $"""
API error: {(int)response.StatusCode} {response.StatusCode}

{errorText}
""";
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var result = new StringBuilder();

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith(":"))
                continue; // DeepSeek keep-alive comment

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
                        result.Append(content);
                }
            }
            catch
            {
                // Ignore broken SSE chunk
            }
        }

        return result.ToString();
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
