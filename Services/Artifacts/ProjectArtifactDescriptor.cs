namespace ICOGenerator.Services.Artifacts;

public record ProjectArtifactDescriptor(
    string Key,
    string FileName,
    string DisplayName,
    bool RequiredForApproval);
