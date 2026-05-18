using System.Text.Json.Serialization;

namespace ICOGenerator.Services.Models;

public class ChatMessageDto
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}
public class ChatCompletionRequestDto
{
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("messages")] public List<ChatMessageDto> Messages { get; set; } = [];
    [JsonPropertyName("temperature")] public double Temperature { get; set; }
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 4096;
}
public class ChatCompletionResponseDto
{
    [JsonPropertyName("choices")] public List<ChatChoiceDto> Choices { get; set; } = [];
}
public class ChatChoiceDto
{
    [JsonPropertyName("message")] public ChatMessageDto Message { get; set; } = new();
}
public class AgentActionDto
{
    public string Type { get; set; } = string.Empty;
    public string? Tool { get; set; }
    public Dictionary<string, System.Text.Json.JsonElement> Args { get; set; } = [];
    public string? Content { get; set; }
}
