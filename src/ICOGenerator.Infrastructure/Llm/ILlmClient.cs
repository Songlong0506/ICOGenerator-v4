using ICOGenerator.Domain;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Models;

namespace ICOGenerator.Services.Llm;

public interface ILlmClient
{
    Task<string> ChatAsync(AiModel model, List<ChatMessageDto> messages, double temperature);
    Task<LocalLlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessageDto> messages, double temperature);
}
