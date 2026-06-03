using ICOGenerator.Application.Abstractions;

namespace ICOGenerator.Application.Requirements;

public class GenerateRequirementDraftUseCase
{
    private readonly IBARequirementService _baRequirementService;

    public GenerateRequirementDraftUseCase(IBARequirementService baRequirementService)
    {
        _baRequirementService = baRequirementService;
    }

    public Task ExecuteAsync(Guid projectId, string message) =>
        _baRequirementService.GenerateOrUpdateDraftAsync(projectId, message);
}
