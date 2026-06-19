namespace ICOGenerator.Services.Artifacts;

public static class ProjectWorkspaceLayout
{
    public static readonly IReadOnlyList<string> Phases =
    [
        "01_Requirement",
        "02_Design",
        "03_Architecture",
        "04_Implementation",
        "05_Test"
    ];
}
