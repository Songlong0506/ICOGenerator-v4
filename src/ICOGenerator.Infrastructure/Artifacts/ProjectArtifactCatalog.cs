namespace ICOGenerator.Services.Artifacts;

public class ProjectArtifactCatalog : IProjectArtifactCatalog
{
    private static readonly ProjectArtifactDescriptor[] Documents =
    [
        new("BRD", "BRD.docx", "Business Requirement Document", true),
        new("SRS", "SRS.docx", "Software Requirement Specification", true),
        new("FSD", "FSD.docx", "Functional Specification Document", true),
        new("UserStories", "UserStories.docx", "User Stories", true),
        new("AIDesignSpec", "AIDesignSpec.docx", "AI Design Spec", true)
    ];

    public IReadOnlyList<ProjectArtifactDescriptor> RequirementDocuments => Documents;
    public ProjectArtifactDescriptor AiDesignSpec => Documents.First(x => x.Key == "AIDesignSpec");
}
