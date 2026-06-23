using System.Text.Json.Serialization;

namespace ICOGenerator.Services.Llm;

// Input contract for a chat turn passed to ILlmClient. Mapped to Microsoft.Extensions.AI's ChatMessage
// inside LlmClient; the wire request is produced by the OpenAI SDK, and the "thinking":{"type":"disabled"}
// field is injected by ThinkingDisabledHandler in the HttpClient pipeline (see OpenAIChatClientFactory).
public class ChatMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
