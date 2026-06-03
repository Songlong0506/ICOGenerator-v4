using ICOGenerator.Domain;
using ICOGenerator.Services.Models;

namespace ICOGenerator.Services.Llm;

public interface ILlmClient
{
    Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessageDto> messages, double temperature);
}
