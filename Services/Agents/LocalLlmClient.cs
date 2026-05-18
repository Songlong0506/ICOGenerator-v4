using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ICOGenerator.Domain;
using ICOGenerator.Services.Models;

namespace ICOGenerator.Services.Agents;

public class LocalLlmClient
{
    public async Task<string> ChatAsync(AiModel model, List<ChatMessageDto> messages, double temperature)
    {
        using var http = new HttpClient { BaseAddress = new Uri(model.Endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", string.IsNullOrWhiteSpace(model.ApiKey) ? "lm-studio" : model.ApiKey);
        var request = new ChatCompletionRequestDto { Model = model.ModelId, Messages = messages, Temperature = temperature, MaxTokens = 23000 };
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("chat/completions", content);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return $"{{\"type\":\"final\",\"content\":\"API error: {response.StatusCode} {Escape(text)}\"}}";
        var parsed = JsonSerializer.Deserialize<ChatCompletionResponseDto>(text);
        return parsed?.Choices.FirstOrDefault()?.Message.Content ?? string.Empty;
    }
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
}
