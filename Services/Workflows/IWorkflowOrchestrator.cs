namespace ICOGenerator.Services.Workflows;

public interface IWorkflowOrchestrator
{
    Task<Guid> StartDeliveryWorkflowAsync(Guid projectId, string requirementVersionName, string aiDesignSpecContent);

    Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId);

    Task<Guid> StartTechnicalDocsWorkflowAsync(Guid projectId);
}
