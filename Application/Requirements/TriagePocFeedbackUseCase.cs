using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Application.Requirements;

public enum PocFeedbackTriageStatus { Ok, NoOpenComments, ProjectNotFound }

/// <summary>Một ghi chú Open kèm ĐỀ XUẤT đường xử lý — người dùng còn đổi được trước khi gửi.</summary>
public record PocFeedbackTriageItem(
    Guid Id,
    int Index,
    string PageView,
    string ElementLabel,
    string Comment,
    bool IsRequirementIssue,
    string Reason);

/// <param name="Classified">
/// false ⇒ máy KHÔNG phân loại được (chưa cấu hình BA, hoặc lời gọi model hỏng): mọi ghi chú rơi về nhóm
/// "chỉnh bản demo" và hộp xác nhận phải nói rõ đây là mặc định an toàn chứ không phải kết luận của máy.
/// </param>
/// <param name="BaConfigured">
/// false ⇒ không có agent BA nên đường "gửi về Requirement" không chạy được; hộp xác nhận khoá nhóm đó
/// lại thay vì để người dùng chọn rồi mới báo lỗi.
/// </param>
public record PocFeedbackTriageReport(
    PocFeedbackTriageStatus Status,
    IReadOnlyList<PocFeedbackTriageItem> Items,
    bool Classified,
    bool BaConfigured);

/// <summary>
/// BƯỚC XEM TRƯỚC của một lượt gửi ghi chú POC: phân loại từng ghi chú Open thành "lỗi trình bày của bản
/// demo" (Developer vá HTML) hay "tài liệu yêu cầu hiểu sai" (BA soạn lại rồi dựng POC mới).
///
/// Vì sao máy phân loại chứ không phải người dùng: trang POC Review trước đây bày HAI nút và bắt người
/// xem demo — dân nghiệp vụ — tự quyết định ghi chú của họ thuộc loại nào, trong khi hai đường đó có chi
/// phí lệch hẳn nhau (một vòng vá HTML có trần, so với soạn lại toàn bộ tài liệu và dựng lại POC). Đúng
/// phép phân loại ấy hệ thống đã làm được bằng prompt, nên để máy đề xuất rồi người dùng soát lại.
///
/// KHÔNG tự khởi động gì: use case này chỉ đọc + gọi model. Mọi thay đổi trạng thái nằm ở
/// <see cref="DispatchPocFeedbackUseCase"/>, sau khi người dùng bấm xác nhận.
///
/// Fail-safe (không fail-open): model hỏng ⇒ mọi ghi chú về nhóm RẺ ("chỉnh bản demo") kèm
/// <see cref="PocFeedbackTriageReport.Classified"/> = false. Không bao giờ tự đề xuất đường đắt bằng một
/// kết quả phân loại mà chính mình không có.
/// </summary>
public class TriagePocFeedbackUseCase
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly BAAgentResolver _agentResolver;

    public TriagePocFeedbackUseCase(
        AppDbContext db, ILlmClient llm, PromptTemplateService prompts, BAAgentResolver agentResolver)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _agentResolver = agentResolver;
    }

    public async Task<PocFeedbackTriageReport> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var projectExists = await _db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, cancellationToken);
        if (!projectExists)
            return new PocFeedbackTriageReport(PocFeedbackTriageStatus.ProjectNotFound, Array.Empty<PocFeedbackTriageItem>(), false, false);

        var open = await _db.PocComments.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Status == PocCommentStatus.Open)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
            return new PocFeedbackTriageReport(PocFeedbackTriageStatus.NoOpenComments, Array.Empty<PocFeedbackTriageItem>(), false, true);

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return new PocFeedbackTriageReport(PocFeedbackTriageStatus.Ok, Fallback(open), Classified: false, BaConfigured: false);

        // Số thứ tự 1-based ở đây là hợp đồng duy nhất giữa prompt và code: model trả về index, ta map
        // ngược về Id theo CÙNG thứ tự này.
        var sb = new StringBuilder();
        sb.AppendLine("## Các ghi chú người dùng ghim trên POC khi review");
        for (var i = 0; i < open.Count; i++)
        {
            var c = open[i];
            sb.Append(i + 1).Append(". ");
            if (!string.IsNullOrWhiteSpace(c.PageView))
                sb.Append($"[Màn hình \"{c.PageView}\"] ");
            if (!string.IsNullOrWhiteSpace(c.ElementLabel))
                sb.Append($"Phần tử: {c.ElementLabel} — ");
            sb.AppendLine(c.Comment.Trim());
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/poc-feedback-triage.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var (callResult, triaged) = await _llm.ChatStructuredAsync<PocFeedbackTriageResult>(
            ba.AiModel!, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAPocFeedbackTriage"),
            cancellationToken: cancellationToken);

        if (!callResult.IsSuccess || triaged == null || triaged.Items.Count == 0)
            return new PocFeedbackTriageReport(PocFeedbackTriageStatus.Ok, Fallback(open), Classified: false, BaConfigured: true);

        var byIndex = triaged.Items
            .Where(v => v.Index >= 1 && v.Index <= open.Count)
            .GroupBy(v => v.Index)
            .ToDictionary(g => g.Key, g => g.First());

        var items = new List<PocFeedbackTriageItem>(open.Count);
        for (var i = 0; i < open.Count; i++)
        {
            var c = open[i];
            // Ghi chú model bỏ sót ⇒ về nhóm rẻ, không có lý do — cùng hướng an toàn với nhánh fail ở trên.
            byIndex.TryGetValue(i + 1, out var verdict);
            items.Add(new PocFeedbackTriageItem(
                c.Id, i + 1, c.PageView, c.ElementLabel, c.Comment,
                verdict?.IsRequirementIssue ?? false,
                verdict?.Reason?.Trim() ?? string.Empty));
        }

        return new PocFeedbackTriageReport(PocFeedbackTriageStatus.Ok, items, Classified: true, BaConfigured: true);
    }

    private static List<PocFeedbackTriageItem> Fallback(IReadOnlyList<Domain.PocComment> open) =>
        open.Select((c, i) => new PocFeedbackTriageItem(
            c.Id, i + 1, c.PageView, c.ElementLabel, c.Comment, IsRequirementIssue: false, Reason: string.Empty))
            .ToList();
}
