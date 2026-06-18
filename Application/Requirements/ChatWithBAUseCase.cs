using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Application.Requirements;

public class ChatWithBAUseCase
{
    private readonly BARequirementService _baRequirementService;

    public ChatWithBAUseCase(BARequirementService baRequirementService)
    {
        _baRequirementService = baRequirementService;
    }

    public Task<ChatWithBAResult> ExecuteAsync(Guid projectId, string message) =>
        _baRequirementService.ChatAsync(projectId, message);
}
