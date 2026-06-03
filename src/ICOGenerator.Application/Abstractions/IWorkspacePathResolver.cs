namespace ICOGenerator.Application.Abstractions;

public interface IWorkspacePathResolver
{
    string GetProjectWorkspacePath(string projectName);
    string GetProjectDocsPath(string projectName);
    string GetDraftDocsPath(string projectName);
    string GetVersionDocsPath(string projectName, string versionName);
    string GetMockupPath(string projectName);
    string GetSafeFullPath(string workspacePath, string relativePath);
}
