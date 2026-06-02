namespace ICOGenerator.Application.Abstractions;

public interface IBARequirementService
{
    Task GenerateOrUpdateDraftAsync(Guid projectId, string userMessage);
}
