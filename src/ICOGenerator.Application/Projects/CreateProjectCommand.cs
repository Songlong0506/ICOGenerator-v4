namespace ICOGenerator.Application.Projects;

public sealed record CreateProjectCommand(
    string Name,
    string Description,
    string GenerationMode,
    string BackendGitUrl,
    string FrontendGitUrl);
