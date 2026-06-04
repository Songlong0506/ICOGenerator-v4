using System.Text.Json.Serialization;

namespace ICOGenerator.Services.Llm;

public class ChatMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

public class ChatCompletionRequestDto
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<ChatMessageDto> Messages { get; set; } = [];

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 23000;

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;

    [JsonPropertyName("thinking")]
    public ThinkingDto? Thinking { get; set; }
}

public class ThinkingDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "disabled";
}

public class ChatCompletionResponseDto
{
    [JsonPropertyName("choices")]
    public List<ChatChoiceDto> Choices { get; set; } = [];
}

public class ChatChoiceDto
{
    [JsonPropertyName("message")]
    public ChatMessageDto Message { get; set; } = new();
}
