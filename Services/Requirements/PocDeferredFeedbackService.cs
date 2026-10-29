using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Đưa các ghi chú CHỈNH TRÌNH BÀY bị hoãn (<see cref="PocCommentStatus.Deferred"/>) vào vòng DỰNG MỚI bản
/// demo kế tiếp.
///
/// Bài toán: một lượt "Gửi ghi chú cho đội xử lý" có cả hai nhóm thì chỉ đường tài liệu chạy (xem
/// <c>DispatchPocFeedbackUseCase</c>) — POC sắp dựng lại từ Brief/Spec đã sửa nên vá HTML lúc đó là vá lên
/// bản sắp bị ghi đè. Nhưng nếu chỉ giữ chúng Open thì người dùng phải tự nhớ gửi lại sau khi duyệt lại
/// requirement, và cho tới lúc đó bản demo mới vẫn mắc đúng các lỗi trình bày họ đã chỉ ra.
///
/// Vì sao áp ở vòng DỰNG MỚI chứ không chạy một vòng chỉnh sửa riêng sau đó: (a) lúc duyệt requirement
/// chưa có bản demo nào để vá — vòng dựng mới ghi đè <c>poc-demo.html</c> về template; (b) chạy riêng tốn
/// một lượt trong trần <c>DeliveryPipeline.MaxRevisionRounds</c> trước khi người dùng kịp nhìn bản mới;
/// (c) neo của ghi chú (selector, toạ độ) nói về bản demo CŨ — trong prompt dựng mới chúng chỉ còn là
/// chữ (màn hình + phần tử + nội dung), cùng hai rào như bộ quy ước trình bày: chỉ áp khi màn hình còn
/// trong spec, và spec thắng khi mâu thuẫn.
///
/// Vòng đời: hoãn ⇒ <c>Deferred</c>; vòng dựng mới chạy XONG ⇒ <see cref="MarkSentAsync"/> chuyển các ghi
/// chú nó đã mang sang <c>Sent</c> để đi tiếp đúng đường đóng vòng của một lần sửa POC (chắt thành quy ước
/// trình bày rồi <c>Addressed</c> — người dùng kiểm lại bằng nút "vẫn chưa đạt"). Vòng dựng hỏng ⇒ không
/// đổi gì, ghi chú chờ vòng dựng sau.
/// </summary>
public class PocDeferredFeedbackService
{
    private readonly AppDbContext _db;
    private readonly ILogger<PocDeferredFeedbackService> _logger;

    public PocDeferredFeedbackService(AppDbContext db, ILogger<PocDeferredFeedbackService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Các ghi chú đang hoãn của project, theo thứ tự ghim.</summary>
    public async Task<IReadOnlyList<PocComment>> LoadAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return await _db.PocComments.AsNoTracking()
            .Where(c => c.ProjectId == projectId
                        && c.Target == PocCommentTarget.Poc
                        && c.Status == PocCommentStatus.Deferred
                        && c.WithdrawnAtUtc == null)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Vòng dựng mới vừa chạy xong với các ghi chú này trong prompt: chuyển chúng sang <c>Sent</c> và nối
    /// <see cref="PocComment.RevisionTaskId"/> về task đó, rồi LƯU NGAY — bước kế của người gọi
    /// (<see cref="PocUiConventionService.TryHarvestAsync"/>) đọc tập <c>Sent</c> thẳng từ DB. Chỉ đụng ghi
    /// chú CÒN <c>Deferred</c>: người dùng có thể đã thu hồi một ghi chú trong lúc bản demo đang dựng.
    /// Best-effort như mọi bước đóng vòng ghi chú: lỗi ở đây không được làm fail một bản demo đã dựng xong
    /// — ghi chú còn <c>Deferred</c> thì vòng dựng sau mang lại.
    /// </summary>
    /// <returns>Số ghi chú đã chuyển.</returns>
    public async Task<int> MarkSentAsync(IReadOnlyCollection<Guid> commentIds, Guid buildTaskId, CancellationToken cancellationToken = default)
    {
        if (commentIds.Count == 0)
            return 0;

        try
        {
            var ids = commentIds.ToList();
            var comments = await _db.PocComments
                .Where(c => ids.Contains(c.Id)
                            && c.Status == PocCommentStatus.Deferred
                            && c.WithdrawnAtUtc == null)
                .ToListAsync(cancellationToken);
            if (comments.Count == 0)
                return 0;

            foreach (var comment in comments)
            {
                comment.Status = PocCommentStatus.Sent;
                comment.Route = PocCommentRoute.FixPoc;
                comment.RevisionTaskId = buildTaskId;
            }

            await _db.SaveChangesAsync(cancellationToken);
            return comments.Count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not close deferred POC comments after build task {TaskId}.", buildTaskId);
            return 0;
        }
    }

    /// <summary>
    /// Khối văn bản NỐI vào prompt dựng POC mới. Không có ghi chú hoãn ⇒ chuỗi rỗng (prompt y như trước).
    /// Cố ý KHÔNG đưa selector: nó trỏ vào bản demo cũ, bản mới dựng từ template nên selector cũ chỉ dắt
    /// agent đi tìm một phần tử không còn tồn tại.
    /// </summary>
    public static string BuildPromptBlock(IReadOnlyList<PocComment>? comments)
    {
        if (comments == null || comments.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("# GÓP Ý TRÌNH BÀY CÒN CHỜ TỪ BẢN DEMO TRƯỚC — ÁP DỤNG NGAY Ở BẢN NÀY");
        sb.AppendLine();
        sb.AppendLine("Người dùng đã ghim các góp ý dưới đây lên bản demo trước. Chúng được hoãn lại vì cùng lúc đó tài liệu yêu cầu đang được sửa và bản demo sắp dựng lại từ đầu — chính là bản bạn đang dựng. Nếu bạn bỏ qua, người dùng sẽ gặp lại đúng những lỗi họ đã chỉ ra.");
        sb.AppendLine("- Áp dụng từng góp ý cho màn hình/phần tử tương ứng trong bản demo mới.");
        sb.AppendLine("- CHỈ áp dụng khi màn hình/phần tử đó CÒN trong AI Design Spec của vòng này. Spec đã bỏ hoặc đổi hẳn chỗ đó thì bỏ qua góp ý — TUYỆT ĐỐI không dựng thêm màn hình chỉ để có chỗ áp dụng.");
        sb.AppendLine("- AI Design Spec luôn THẮNG khi mâu thuẫn: các góp ý này nói về cách TRÌNH BÀY, chúng không thêm/bớt/đổi nghiệp vụ.");
        sb.AppendLine("- Trong bàn giao cuối, nói ngắn gọn góp ý nào đã áp dụng và góp ý nào bỏ qua (kèm lý do).");
        sb.AppendLine();
        PocCommentDigest.AppendBullets(sb, comments);

        return sb.ToString();
    }
}
