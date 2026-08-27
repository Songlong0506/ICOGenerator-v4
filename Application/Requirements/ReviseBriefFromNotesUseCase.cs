using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Application.Requirements;

public enum ReviseBriefResult { Ok, ProjectNotFound, NoNotes, BaNotConfigured }

/// <summary>
/// Biến các ghi chú người dùng ghim trực tiếp lên bản xem trước Product Brief thành MỘT lượt phản hồi
/// trong hội thoại BA, rồi khởi động lại workflow soạn draft — bản Brief mới sẽ sửa đúng các đoạn được
/// chú. Đi qua hội thoại (thay vì sửa thẳng file) để giữ nguyên nguồn sự thật: Brief luôn sinh từ
/// transcript, và ghi chú cũng thành nguồn cho <see cref="ChecklistGapMemoryService"/> như mọi lượt khác.
/// Tái dùng đúng vòng "Write Requirement" hiện có — không thêm đường sinh tài liệu song song.
/// <para>
/// Mỗi ghi chú cũng được LƯU thành một dòng <see cref="PocComment"/> (<see cref="PocCommentTarget.Brief"/>)
/// đóng dấu "draft" — ApproveRequirementUseCase nâng lên V{n} cùng lúc với file. Trước đây chúng chỉ tan
/// vào transcript: sau khi Brief lên bản mới, không còn cách nào biết bản cũ từng bị chê gì, mà transcript
/// thì không phân biệt được lượt nào là ghi chú review với lượt chat thường.
/// </para>
/// </summary>
public class ReviseBriefFromNotesUseCase
{
    private readonly AppDbContext _db;
    private readonly BAConversationLog _conversationLog;
    private readonly BAAgentResolver _agentResolver;
    private readonly GenerateRequirementDraftUseCase _generateDraft;

    public ReviseBriefFromNotesUseCase(
        AppDbContext db,
        BAConversationLog conversationLog,
        BAAgentResolver agentResolver,
        GenerateRequirementDraftUseCase generateDraft)
    {
        _db = db;
        _conversationLog = conversationLog;
        _agentResolver = agentResolver;
        _generateDraft = generateDraft;
    }

    /// <param name="createdByUsername">Người gửi ghi chú — cột "người tạo" của bảng lịch sử.</param>
    public async Task<ReviseBriefResult> ExecuteAsync(
        Guid projectId, IReadOnlyList<BriefNote> notes, string? createdByUsername = null, CancellationToken cancellationToken = default)
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

        // Dòng lịch sử — ghi TRƯỚC khi khởi động lại vòng soạn draft: vòng đó có thể chạy dài và ghi đè
        // bản Brief, còn ghi chú thì phải nằm lại kể cả khi lượt soạn hỏng giữa chừng. Đóng dấu "draft" vì
        // chúng nói về bản đang xem; ApproveRequirementUseCase nâng lên V{n} khi bản ấy được duyệt.
        foreach (var n in clean)
        {
            _db.PocComments.Add(new PocComment
            {
                ProjectId = projectId,
                Target = PocCommentTarget.Brief,
                BriefVersion = BriefVersionResolver.DraftVersion,
                Quote = Clip(n.Quote, 1000),
                Comment = Clip(n.Note, 4000),
                CreatedByUsername = createdByUsername,
                // Ghi chú Brief KHÔNG có đường nào khác: nó đi thẳng vào hội thoại BA để soạn lại tài liệu.
                Status = PocCommentStatus.RoutedToRequirement,
                Route = PocCommentRoute.Requirement
            });
        }
        await _db.SaveChangesAsync(cancellationToken);

        // Lượt user này đi vào transcript → workflow "Write Requirement" soạn lại Brief có tính tới nó.
        // ProjectNotFound: BAConversationLog ghi thẳng với ProjectId; project không tồn tại sẽ ném FK khi
        // SaveChanges. Kiểm tra rẻ hơn: để orchestrator/worker xử lý, nhưng ở đây coi ghi thành công là Ok.
        await _conversationLog.AppendAsync(projectId, ba.Id, "user", sb.ToString().TrimEnd(), cancellationToken: cancellationToken);
        await _generateDraft.ExecuteAsync(projectId);
        return ReviseBriefResult.Ok;
    }

    private static string Clip(string? value, int maxLength)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
