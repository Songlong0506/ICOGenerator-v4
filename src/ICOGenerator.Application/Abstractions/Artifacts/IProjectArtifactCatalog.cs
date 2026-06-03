namespace ICOGenerator.Services.Artifacts;

public interface IProjectArtifactCatalog
{
    IReadOnlyList<ProjectArtifactDescriptor> RequirementDocuments { get; }
    ProjectArtifactDescriptor AiDesignSpec { get; }
}
