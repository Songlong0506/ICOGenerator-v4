using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum AcceptPocResult { Ok, AlreadyAccepted, ProjectNotFound, NoPoc }

/// <summary>
/// NGHIỆM THU BẢN DEMO — trạng thái KẾT của hành trình phía người dùng nghiệp vụ.
///
/// <para>
/// Trước đây tuyến này không có điểm dừng: người yêu cầu xem POC, ghim ghi chú, nhờ Dev chỉnh, gửi điểm
/// hiểu sai về BA… nhưng không có bất kỳ cách nào để nói "bản này được rồi". Cổng duyệt thật nằm ở Agent
/// Dashboard sau quyền <c>DeliveryAdvance</c>, nên đội delivery phải đi hỏi miệng xem người yêu cầu đã
/// ưng chưa, còn chặng cuối của stepper ("Xem &amp; góp ý") thì không bao giờ đóng lại — người dùng làm
/// xong việc của mình mà màn hình vẫn trông như đang dở dang.
/// </para>
/// <para>
/// Nghiệm thu KHÔNG tự đẩy pipeline: nó chỉ ghi lại "ai, lúc nào" và báo cho người có quyền duyệt. Việc
/// đi tiếp vẫn là quyết định của đội delivery ở cổng POC — tách hai thứ này để một cú bấm của người dùng
/// nghiệp vụ không âm thầm khởi động các bước đắt tiền phía sau.
/// </para>
/// </summary>
public class AcceptPocUseCase
{
    private readonly AppDbContext _db;
    private readonly INotificationService _notifications;

    public AcceptPocUseCase(AppDbContext db, INotificationService notifications)
    {
        _db = db;
        _notifications = notifications;
    }

    public async Task<AcceptPocResult> ExecuteAsync(Guid projectId, string acceptedBy, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return AcceptPocResult.ProjectNotFound;

        if (project.PocAcceptedAtUtc.HasValue)
            return AcceptPocResult.AlreadyAccepted;

        // Chỉ nghiệm thu được thứ đã tồn tại: phải có một run delivery đã đi qua (hoặc đang dừng ở) bước
        // POC. Không có gì để nghiệm thu mà vẫn ghi nhận thì cột này mất hết ý nghĩa với đội delivery.
        var deliveryRun = await _db.WorkflowRuns
            .Where(r => r.ProjectId == projectId && r.Name.StartsWith("Delivery Workflow"))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (deliveryRun == null)
            return AcceptPocResult.NoPoc;

        project.PocAcceptedAtUtc = DateTime.UtcNow;
        project.PocAcceptedBy = string.IsNullOrWhiteSpace(acceptedBy) ? null : acceptedBy.Trim();

        // Thông báo chỉ Add entity (hợp đồng của INotificationService) — SaveChanges bên dưới lưu atomic
        // cùng lượt ghi nghiệm thu, nên không có cảnh "đã báo nhưng chưa ghi".
        await _notifications.NotifyPocAcceptedAsync(deliveryRun, project.PocAcceptedBy ?? "Người yêu cầu", cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return AcceptPocResult.Ok;
    }
}
