using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public record AgentDashboardResult(
    Project Project,
    IReadOnlyList<Agent> Agents,
    IReadOnlyList<string> Phases,
    IReadOnlyList<ProjectDocument> WorkspaceDocuments,
    long TotalTokens,
    IReadOnlyDictionary<Guid, long> TokensByAgent,
    IReadOnlyDictionary<Guid, int> CallsByAgent,
    IReadOnlyDictionary<Guid, DateTime> LastActivityByAgent);

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

        var agents = await _db.Agents.AsNoTracking()
            .Include(x => x.AgentTools)
            .ThenInclude(x => x.ToolDefinition)
            .ToListAsync();

        // One pass over the project's call logs yields every per-agent stat the dashboard needs:
        // token totals, call counts, and the most recent activity timestamp.
        var statsByAgent = await _db.AgentModelCallLogs.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .GroupBy(x => x.AgentId)
            .Select(g => new
            {
                AgentId = g.Key,
                Total = g.Sum(x => (long)x.TotalTokens),
                Calls = g.Count(),
                LastActivity = g.Max(x => x.CreatedAt)
            })
            .ToListAsync();

        var tokensByAgent = statsByAgent.ToDictionary(x => x.AgentId, x => x.Total);
        var callsByAgent = statsByAgent.ToDictionary(x => x.AgentId, x => x.Calls);
        var lastActivityByAgent = statsByAgent.ToDictionary(x => x.AgentId, x => x.LastActivity);
        var totalTokens = statsByAgent.Sum(x => x.Total);

        var workspaceDocuments = LoadWorkspaceDocuments(project, project.Documents);

        return new AgentDashboardResult(project, agents, ProjectWorkspaceLayout.Phases, workspaceDocuments, totalTokens, tokensByAgent, callsByAgent, lastActivityByAgent);
    }

    private IReadOnlyList<ProjectDocument> LoadWorkspaceDocuments(Project project, IEnumerable<ProjectDocument> databaseDocuments)
    {
        var documents = databaseDocuments.ToList();
        var workspacePath = _workspacePathResolver.GetProjectWorkspacePath(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));

        if (!Directory.Exists(workspacePath))
            return documents;

        var knownDocumentKeys = documents
            .Select(x => GetDocumentKey(x.Folder, x.VersionName, x.FileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Skip regenerable dirs (node_modules/bin/obj/.git/.vs): once a project has run npm install /
        // dotnet build, those hold tens of thousands of files that would otherwise be enumerated AND
        // read synchronously (up to 12 KB each) on every dashboard load — and rendered as document cards.
        foreach (var filePath in Directory.EnumerateFiles(workspacePath, "*.*", SearchOption.AllDirectories)
                     .Where(x => !WorkspaceFileFilter.IsInRegenerableDirectory(workspacePath, x)))
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

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".css", ".csv", ".html", ".htm", ".js", ".json", ".md", ".sql", ".txt", ".xml", ".yml", ".yaml"
    };

    private static string ReadPreviewContent(string filePath)
    {
        var extension = Path.GetExtension(filePath);

        if (!TextExtensions.Contains(extension))
            return $"Preview is not available for binary file: {Path.GetFileName(filePath)}";

        // A locked/deleted/permission-denied file must not 500 the whole dashboard; degrade to an
        // inline note for that one file and keep rendering the rest.
        try
        {
            var content = File.ReadAllText(filePath);
            return content.Length > 12000 ? content[..12000] + "\n...[truncated]" : content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Preview unavailable ({Path.GetFileName(filePath)}): {ex.Message}";
        }
    }
}
