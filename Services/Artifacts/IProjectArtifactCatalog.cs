namespace ICOGenerator.Services.Artifacts;

public interface IProjectArtifactCatalog
{
    /// <summary>Tài liệu dễ hiểu cho user thường (sinh khi bấm "Write Requirement").</summary>
    ProjectArtifactDescriptor ProductBrief { get; }

    /// <summary>Bản kỹ thuật cho AI Developer Agent dựng POC.</summary>
    ProjectArtifactDescriptor AiDesignSpec { get; }

    /// <summary>Tài liệu kỹ thuật nặng (BRD/SRS/FSD/UserStories) — do team dev trigger ở Agent Dashboard.</summary>
    IReadOnlyList<ProjectArtifactDescriptor> TechnicalDocuments { get; }
}
