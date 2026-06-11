using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Application.Requirements;

public class GenerateRequirementDraftUseCase
{
    private readonly BARequirementService _baRequirementService;

    public GenerateRequirementDraftUseCase(BARequirementService baRequirementService)
    {
        _baRequirementService = baRequirementService;
    }

    public Task ExecuteAsync(Guid projectId) =>
        _baRequirementService.GenerateOrUpdateDraftAsync(projectId);
}
