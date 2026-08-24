using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Application.Requirements;

public enum ReviseBriefResult { Ok, ProjectNotFound, NoNotes, BaNotConfigured }

/// <summary>
/// Biến các ghi chú người dùng ghim trực tiếp lên bản xem trước Product Brief thành hai thứ: MỘT lượt phản
/// hồi trong hội thoại BA (để ghi chú nằm trong transcript như mọi lời người dùng khác — các lượt sau,
/// bản đồ bao phủ và <see cref="ChecklistGapMemoryService"/> đều thấy nó), và một run "Write Requirement"
/// mang chính các ghi chú đó làm <c>AgentTask.Input</c>.
///
/// Run đó chạy vòng SỬA CÓ PHẠM VI (<see cref="ProductBriefDraftService.ReviseDraftFromNotesAsync"/>):
/// giữ nguyên bản Brief hiện có, chỉ sửa các đoạn được chú. Trước đây đường này gọi thẳng lượt soạn tài
/// liệu, nên một ghi chú một dòng kéo theo một lần VIẾT LẠI cả tài liệu từ transcript — người dùng nhận
/// về hàng chục dòng đổi ngoài ý mình và mất lòng tin vào nút này.
///
/// Vẫn tái dùng đúng vòng "Write Requirement" hiện có (cùng loại run, cùng panel tiến độ) — không thêm
/// đường sinh tài liệu song song.
/// </summary>
public class ReviseBriefFromNotesUseCase
{
    private readonly BAConversationLog _conversationLog;
    private readonly BAAgentResolver _agentResolver;
    private readonly GenerateRequirementDraftUseCase _generateDraft;

    public ReviseBriefFromNotesUseCase(
        BAConversationLog conversationLog,
        BAAgentResolver agentResolver,
        GenerateRequirementDraftUseCase generateDraft)
    {
        _conversationLog = conversationLog;
        _agentResolver = agentResolver;
        _generateDraft = generateDraft;
    }

    public async Task<ReviseBriefResult> ExecuteAsync(Guid projectId, IReadOnlyList<BriefNote> notes, CancellationToken cancellationToken = default)
    {
        var clean = (notes ?? Array.Empty<BriefNote>())
            .Where(n => !string.IsNullOrWhiteSpace(n.Note))
            .Take(30)
            .ToList();
        if (clean.Count == 0)
            return ReviseBriefResult.NoNotes;

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return ReviseBriefResult.BaNotConfigured;

        var sb = new StringBuilder();
        sb.AppendLine("Tôi đã xem bản mô tả sản phẩm (Product Brief) và muốn chỉnh các điểm sau:");
        foreach (var n in clean)
        {
            var quote = n.Quote.Trim();
            if (quote.Length > 200)
                quote = quote[..200] + "…";
            if (quote.Length > 0)
                sb.AppendLine($"- Ở đoạn “{quote}”: {n.Note.Trim()}");
            else
                sb.AppendLine($"- {n.Note.Trim()}");
        }
        sb.AppendLine("Hãy cập nhật lại bản mô tả sản phẩm theo đúng các ý này.");

        // Lượt user này đi vào transcript để ghi chú không "nằm ngoài hội thoại" (các lượt chat sau, bản đồ
        // bao phủ và lượt soạn lại đầy đủ về sau đều phải thấy nó). Bản thân việc SỬA tài liệu thì không
        // đọc lại transcript để dựng bản mới — nó đi theo payload dưới đây.
        // ProjectNotFound: BAConversationLog ghi thẳng với ProjectId; project không tồn tại sẽ ném FK khi
        // SaveChanges. Kiểm tra rẻ hơn: để orchestrator/worker xử lý, nhưng ở đây coi ghi thành công là Ok.
        await _conversationLog.AppendAsync(projectId, ba.Id, "user", sb.ToString().TrimEnd(), cancellationToken: cancellationToken);

        // Ghi chú đi theo run dưới dạng dữ liệu có cấu trúc (không phải bằng cách bắt worker đoán lại từ
        // transcript): worker mới biết ĐÚNG đoạn nào được chú để chỉ sửa những chỗ đó.
        await _generateDraft.ExecuteAsync(projectId, briefNotesPayload: BriefNotePayload.Serialize(clean));
        return ReviseBriefResult.Ok;
    }
}
