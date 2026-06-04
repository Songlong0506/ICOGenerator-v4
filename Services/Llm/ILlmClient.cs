using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

public interface ILlmClient
{
    Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessageDto> messages, double temperature);
}
