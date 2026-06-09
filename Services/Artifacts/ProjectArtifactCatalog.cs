namespace ICOGenerator.Services.Artifacts;

public class ProjectArtifactCatalog : IProjectArtifactCatalog
{
    private static readonly ProjectArtifactDescriptor[] Documents =
    [
        new("BRD", "BRD.docx", "Business Requirement Document", true, "01_Requirement"),
        new("SRS", "SRS.docx", "Software Requirement Specification", true, "01_Requirement"),
        new("FSD", "FSD.docx", "Functional Specification Document", true, "01_Requirement"),
        new("UserStories", "UserStories.docx", "User Stories", true, "01_Requirement"),
        new("AIDesignSpec", "AIDesignSpec.docx", "AI Design Spec", true, "02_Design")
    ];

    public IReadOnlyList<ProjectArtifactDescriptor> RequirementDocuments => Documents;
    public ProjectArtifactDescriptor AiDesignSpec => Documents.First(x => x.Key == "AIDesignSpec");
}
