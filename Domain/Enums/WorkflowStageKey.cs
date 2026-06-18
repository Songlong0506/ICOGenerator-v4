namespace ICOGenerator.Domain.Enums;

public enum WorkflowStageKey
{
    RequirementApproved = 1,
    Implementation = 2,
    Completed = 3,
    Failed = 4,
    // Stored as strings (HasConversion<string>), so appended values need no migration. New numbers
    // keep the existing four stable; the pipeline order lives in DeliveryPipeline, not these numbers.
    ArchitectureDesign = 5,
    Testing = 6
}
