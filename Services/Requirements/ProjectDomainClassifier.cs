using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Phân loại dự án vào MỘT miền nghiệp vụ trong taxonomy cố định (xem
/// <c>Prompts/BusinessAnalyst/project-domain.v1.md</c>) và lưu vào <see cref="Domain.Project.DomainKey"/>.
/// Miền quyết định BUCKET "checklist học được" nào của BA được nạp/ghi (<see cref="ChecklistNoteStore"/>).
/// Chạy MỘT LẦN cho mỗi dự án, ở hậu kỳ lượt chat (sau frame done) hoặc trước vòng harvest — không bao
/// giờ nằm trên đường trả lời user. Fail-open toàn phần: phân loại lỗi thì DomainKey giữ null (bucket
/// chung vẫn hoạt động), lần gọi sau thử lại.
/// </summary>
public class ProjectDomainClassifier
{
    // Taxonomy cố định — slug tự do của model dễ vỡ vụn bucket ("nghi-phep" vs "leave"). Phải khớp
    // danh sách trong project-domain.v1.md.
    public static readonly IReadOnlySet<string> KnownDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "leave-management", "hr-people", "inventory", "procurement", "sales-crm", "finance",
        "project-tracking", "booking", "document-workflow", "reporting-dashboard", "training",
        "quality-audit", "other"
    };

    // Chỉ cần phần đầu hội thoại để nhận diện miền — gửi cả hội thoại dài là phí token vô ích.
    private const int MaxTurnsForClassification = 12;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly BAAgentResolver _agentResolver;
    private readonly ILogger<ProjectDomainClassifier> _logger;

    public ProjectDomainClassifier(
        AppDbContext db,
        ILlmClient llm,
        PromptTemplateService prompts,
        BAAgentResolver agentResolver,
        ILogger<ProjectDomainClassifier> logger)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _agentResolver = agentResolver;
        _logger = logger;
    }

    /// <summary>
    /// Phân loại nếu dự án chưa có miền và đã có ít nhất một lượt user. Idempotent — đã có miền thì
    /// thoát ngay (một query). Mọi lỗi đều nuốt + log.
    /// </summary>
    public async Task TryClassifyAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
            if (project == null || !string.IsNullOrEmpty(project.DomainKey))
                return;

            var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
            if (ba == null)
                return;

            var turns = await _db.AgentConversations
                .AsNoTracking()
                .Where(c => c.ProjectId == projectId)
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .Take(MaxTurnsForClassification)
                .ToListAsync(cancellationToken);
            if (!turns.Any(t => t.Role != "assistant"))
                return; // chưa có gì của user để phân loại.

            var transcript = ConversationTranscriptBuilder.Build(turns);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _prompts.Get("BusinessAnalyst/project-domain.v1.md")),
                new(ChatRole.User, $"Project: {project.Name}\n\nMô tả: {project.Description}\n\nPhần đầu hội thoại:\n{transcript}")
            };

            var (result, structured) = await _llm.ChatStructuredAsync<ProjectDomainResult>(
                ba.AiModel!, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAProjectDomain"),
                cancellationToken: cancellationToken);
            if (!result.IsSuccess)
                return;

            var key = structured?.DomainKey.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(key) || !KnownDomains.Contains(key))
                return; // slug lạ ⇒ coi như chưa phân loại được, lần sau thử lại.

            project.DomainKey = key;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not classify domain for project {ProjectId}.", projectId);
        }
    }
}
