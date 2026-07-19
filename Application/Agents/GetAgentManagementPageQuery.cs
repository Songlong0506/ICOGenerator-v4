using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public class GetAgentManagementPageQuery
{
    private readonly AppDbContext _db;
    private readonly PromptFileCatalog _catalog;

    // Thư mục prompt trùng tên với AgentRoleKey mới thuộc về một agent; phần còn lại (Shared, Eval,
    // Design, ...) gộp vào mục "Shared / General".
    private static readonly HashSet<string> RoleFolders =
        new(Enum.GetNames<AgentRoleKey>(), StringComparer.OrdinalIgnoreCase);

    public GetAgentManagementPageQuery(AppDbContext db, PromptFileCatalog catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<AgentManagementPage> ExecuteAsync(Guid? id, bool shared = false)
    {
        var agents = await _db.Agents
            .AsNoTracking()
            .Include(x => x.AiModel)
            .Include(x => x.AgentTools)
            .ThenInclude(x => x.ToolDefinition)
            .OrderBy(x => x.RoleKey)
            .ToListAsync();

        var models = await _db.AiModels.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.ModelId).ToListAsync();
        var tools = await _db.ToolDefinitions.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.DisplayName).ToListAsync();

        // Chỉ kéo metadata (không Content — cột LOB) để thống kê phiên bản cho mỗi prompt key.
        var versions = await _db.PromptTemplateVersions.AsNoTracking()
            .Select(v => new { v.PromptKey, v.VersionNumber, v.IsActive, v.CreatedAt, v.CreatedByUsername })
            .ToListAsync();

        var statsByKey = versions
            .GroupBy(v => v.PromptKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var latest = g.OrderByDescending(v => v.VersionNumber).First();
                return (
                    Count: g.Count(),
                    Active: g.Where(v => v.IsActive).Select(v => (int?)v.VersionNumber).FirstOrDefault(),
                    LastAt: (DateTime?)latest.CreatedAt,
                    LastBy: latest.CreatedByUsername);
            }, StringComparer.OrdinalIgnoreCase);

        // Mọi prompt key: file .md dưới /Prompts + key chỉ còn sống bằng phiên bản DB (file đã xoá).
        var fileKeys = _catalog.PromptKeys;
        var allKeys = fileKeys
            .Concat(statsByKey.Keys.Where(k => !fileKeys.Contains(k, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        if (shared)
        {
            var sharedPrompts = allKeys
                .Where(k => !RoleFolders.Contains(FolderOf(k)))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(Build)
                .ToList();
            return new AgentManagementPage(agents, null, models, tools, sharedPrompts, SharedSelected: true);
        }

        var selected = id.HasValue ? agents.FirstOrDefault(x => x.Id == id) : agents.FirstOrDefault();

        IReadOnlyList<AgentPromptItem> prompts = selected == null
            ? Array.Empty<AgentPromptItem>()
            : allKeys
                .Where(k => string.Equals(FolderOf(k), selected.RoleKey.ToString(), StringComparison.OrdinalIgnoreCase))
                .Select(Build)
                // instruction.md là chỉ dẫn cốt lõi của agent — đưa lên đầu, còn lại theo tên.
                .OrderByDescending(p => p.IsInstruction)
                .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        return new AgentManagementPage(agents, selected, models, tools, prompts, SharedSelected: false);

        AgentPromptItem Build(string key)
        {
            var slash = key.IndexOf('/');
            var displayName = slash >= 0 ? key[(slash + 1)..] : key;
            var stats = statsByKey.GetValueOrDefault(key);
            return new AgentPromptItem(
                key,
                displayName,
                IsInstruction: displayName.Equals("instruction.md", StringComparison.OrdinalIgnoreCase),
                FileExists: fileKeys.Contains(key, StringComparer.OrdinalIgnoreCase),
                VersionCount: stats.Count,
                ActiveVersionNumber: stats.Active,
                LastChangedAt: stats.LastAt,
                LastChangedBy: stats.LastBy);
        }
    }

    private static string FolderOf(string promptKey)
    {
        var slash = promptKey.IndexOf('/');
        return slash >= 0 ? promptKey[..slash] : string.Empty;
    }
}
