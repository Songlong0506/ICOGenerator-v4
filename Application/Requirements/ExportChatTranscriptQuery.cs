using ICOGenerator.Data;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public record ChatTranscriptExport(string FileName, string Content);

/// <summary>
/// Xuất cuộc trò chuyện với BA thành MỘT file Markdown tự chứa, để người dùng đem sang một AI khác nhờ
/// rà soát chất lượng buổi phỏng vấn (xem <see cref="ChatExportBuilder"/> về việc file gồm những gì và
/// vì sao chép riêng các bong bóng chat là không đủ).
/// </summary>
public class ExportChatTranscriptQuery
{
    /// <summary>Prompt hệ thống của BA — đính vào phụ lục làm chuẩn để chấm cuộc phỏng vấn.</summary>
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    /// <summary>Chỉ dẫn cho AI đọc bản xuất. Là file prompt nên sửa được ở Prompt Studio, không cần deploy.</summary>
    private const string ReviewBriefKey = "Eval/chat-review.v1.md";

    private readonly AppDbContext _db;
    private readonly PromptTemplateService _prompts;
    private readonly OrganizationContextService _orgContext;
    private readonly BAAgentResolver _baAgents;

    public ExportChatTranscriptQuery(
        AppDbContext db,
        PromptTemplateService prompts,
        OrganizationContextService orgContext,
        BAAgentResolver baAgents)
    {
        _db = db;
        _prompts = prompts;
        _orgContext = orgContext;
        _baAgents = baAgents;
    }

    public async Task<ChatTranscriptExport?> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);

        if (project == null)
            return null;

        // Chỉ hội thoại ĐANG dùng — global query filter (ArchivedAt == null) đã lọc sẵn, như mọi đường
        // đọc khác. Các lượt đã bị "New Chat" lưu trữ chỉ được nêu bằng CON SỐ trong bản xuất: đưa cả vào
        // thì người chấm chấm một cuộc phỏng vấn mà chính BA không còn nhìn thấy.
        var turns = await _db.AgentConversations
            .AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

        // Đếm phần lưu trữ thì phải TẮT chính bộ lọc đó — không có IgnoreQueryFilters, con số này luôn 0
        // và bản xuất im lặng nói dối rằng hội thoại này là tất cả những gì đã diễn ra.
        var archivedTurnCount = await _db.AgentConversations
            .IgnoreQueryFilters()
            .CountAsync(c => c.ProjectId == projectId && c.ArchivedAt != null, cancellationToken);

        var sources = await _db.ProjectSourceFiles
            .AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        // Cùng đường tra agent BA với luồng chat (thứ tự ổn định) — bản xuất phải nói đúng model đã tạo ra
        // các lượt này, nếu không thì mọi kết luận "model kém" chỉ tay nhầm chỗ.
        var ba = await _baAgents.FindConfiguredAsync(cancellationToken);

        // Hồ sơ người dùng gắn theo NGƯỜI TẠO dự án (như UserMemoryService) — nó được nạp vào mọi lượt
        // chat nên là một phần lời giải thích cho cách BA hành xử.
        string? userMemory = null;
        if (!string.IsNullOrWhiteSpace(project.CreatedByUsername))
        {
            userMemory = await _db.AppUsers
                .AsNoTracking()
                .Where(u => u.Username == project.CreatedByUsername)
                .Select(u => u.UserMemory)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var snapshot = new ChatExportSnapshot(
            project,
            turns,
            archivedTurnCount,
            sources,
            await BuildOrgUnitNoteAsync(project.OrgUnitCode, cancellationToken),
            ba?.Description,
            ba?.AiModel?.ModelId,
            ba?.AiModel?.SupportsVision ?? false,
            userMemory,
            _prompts.Get(ReviewBriefKey),
            _prompts.Get(ChatPromptKey),
            await BuildOrganizationContextAsync(project.OrgUnitCode, cancellationToken),
            DateTime.UtcNow);

        return new ChatTranscriptExport(
            ExportFileName.Build("chat-ba", project.Name, snapshot.ExportedAtUtc, ".md"),
            ChatExportBuilder.Build(snapshot));
    }

    /// <summary>
    /// Đúng khối ngữ cảnh mà <c>BAChatService</c> / <c>ProductBriefDraftService</c> đính vào mọi lời gọi BA
    /// — ranh giới phạm vi, nền tảng đã chốt, danh sách department/HoD, đơn vị yêu cầu.
    ///
    /// <para>
    /// Không phải "cho đầy đủ": đây là NGUỒN của các dữ kiện mà người chấm sẽ soi. Bỏ nó ra thì "nhà máy
    /// Bosch Đồng Nai" hay "email là kênh thông báo duy nhất" trong Product Brief trông y hệt một câu bịa
    /// thêm không truy được nguồn — trong khi chúng là hằng số của sản phẩm và BA dùng chúng là ĐÚNG.
    /// </para>
    ///
    /// <para>Fail-open như mọi đường ngữ cảnh khác: dựng lỗi ⇒ null, phụ lục B nói rõ là không có.</para>
    /// </summary>
    private async Task<string?> BuildOrganizationContextAsync(string? orgUnitCode, CancellationToken cancellationToken)
    {
        try
        {
            var context = await _orgContext.BuildCombinedContextAsync(orgUnitCode, cancellationToken);
            return string.IsNullOrWhiteSpace(context) ? null : context;
        }
        catch
        {
            return null;
        }
    }

    // Ghi chú "đơn vị yêu cầu" dựng từ dữ liệu HR như lúc chat. Fail-open toàn tuyến (bảng trống/lỗi ⇒
    // bỏ trống dòng đó) — không đáng để hỏng cả bản xuất vì một dòng bối cảnh.
    private async Task<string?> BuildOrgUnitNoteAsync(string? orgUnitCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(orgUnitCode))
            return null;

        try
        {
            return await _orgContext.BuildProjectUnitNoteAsync(orgUnitCode, cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
