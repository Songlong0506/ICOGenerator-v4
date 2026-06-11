using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public record AgentDashboardResult(Project Project, IReadOnlyList<Agent> Agents, IReadOnlyList<string> Phases, IReadOnlyList<ProjectDocument> WorkspaceDocuments);

public class GetAgentDashboardQuery
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;

    public GetAgentDashboardQuery(AppDbContext db, WorkspacePathResolver workspacePathResolver)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
    }

    public async Task<AgentDashboardResult?> ExecuteAsync(Guid projectId)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId);
        if (project == null)
            return null;

        project.Documents = await _db.ProjectDocuments.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Folder)
            .ThenBy(x => x.FileName)
            .ToListAsync();

        project.Conversations = await _db.AgentConversations.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .Include(x => x.Agent)
            .OrderByDescending(x => x.CreatedAt)
            .Take(50)
            .ToListAsync();

        project.ModelCallLogs = await _db.AgentModelCallLogs.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => new AgentModelCallLog
            {
                Id = x.Id,
                ModelName = x.ModelName,
                TotalTokens = x.TotalTokens,
                DurationMs = x.DurationMs,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync();

        var agents = await _db.Agents.AsNoTracking()
            .Include(x => x.AgentTools)
            .ThenInclude(x => x.ToolDefinition)
            .ToListAsync();

        var workspaceDocuments = LoadWorkspaceDocuments(project, project.Documents);

        return new AgentDashboardResult(project, agents, ProjectWorkspaceLayout.Phases, workspaceDocuments);
    }

    private IReadOnlyList<ProjectDocument> LoadWorkspaceDocuments(Project project, IEnumerable<ProjectDocument> databaseDocuments)
    {
        var documents = databaseDocuments.ToList();
        var workspacePath = _workspacePathResolver.GetProjectWorkspacePath(project.Name);

        if (!Directory.Exists(workspacePath))
            return documents;

        var knownDocumentKeys = documents
            .Select(x => GetDocumentKey(x.Folder, x.VersionName, x.FileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in Directory.EnumerateFiles(workspacePath, "*.*", SearchOption.AllDirectories)
                     .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")))
        {
            var relativePath = Path.GetRelativePath(workspacePath, filePath);
            var pathParts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (pathParts.Length == 0)
                continue;

            var phase = ProjectWorkspaceLayout.Phases.FirstOrDefault(x =>
                string.Equals(x, pathParts[0], StringComparison.OrdinalIgnoreCase));
            if (phase == null)
                continue;

            var versionName = pathParts.Length > 2 ? pathParts[1] : string.Empty;
            var fileName = pathParts.Length > 2
                ? string.Join(Path.DirectorySeparatorChar, pathParts.Skip(2))
                : pathParts[^1];

            var documentKey = GetDocumentKey(phase, versionName, fileName);
            if (!knownDocumentKeys.Add(documentKey))
                continue;

            documents.Add(new ProjectDocument
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Folder = phase,
                VersionName = versionName,
                FileName = fileName,
                Content = ReadPreviewContent(filePath),
                FilePath = relativePath,
                CreatedAt = File.GetLastWriteTimeUtc(filePath)
            });
        }

        return documents
            .OrderBy(x => x.Folder)
            .ThenBy(x => x.VersionName)
            .ThenBy(x => x.FileName)
            .ToList();
    }

    private static string GetDocumentKey(string folder, string versionName, string fileName) =>
        $"{folder}/{versionName}/{fileName}";

    private static string ReadPreviewContent(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".css", ".csv", ".html", ".htm", ".js", ".json", ".md", ".sql", ".txt", ".xml", ".yml", ".yaml"
        };

        if (!textExtensions.Contains(extension))
            return $"Preview is not available for binary file: {Path.GetFileName(filePath)}";

        var content = File.ReadAllText(filePath);
        return content.Length > 12000 ? content[..12000] + "\n...[truncated]" : content;
    }
}
