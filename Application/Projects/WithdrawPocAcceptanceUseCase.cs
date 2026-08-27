using ICOGenerator.Data;
using ICOGenerator.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum WithdrawPocAcceptanceResult { Ok, NotAccepted, ProjectNotFound }

/// <summary>
/// RÚT NGHIỆM THU — chiều ngược của <see cref="AcceptPocUseCase"/>, và là cách DUY NHẤT mở lại khoá do
/// nghiệm thu dựng lên (xem <see cref="PocAcceptanceGate"/>).
///
/// <para>
/// Nghiệm thu trước đây một chiều: bấm nhầm là không gỡ được bằng giao diện. Từ khi cú bấm ấy còn ĐÓNG
/// BĂNG chat BA và ghi chú POC thì một chiều là không chấp nhận được — người dùng phát hiện thêm điểm sai
/// sau khi đã bấm "được rồi" sẽ không còn đường nào để nói.
/// </para>
/// <para>
/// Đội delivery đã nhận thông báo "đã nghiệm thu" ở chiều đi, nên chiều về cũng phải báo: họ có thể đang
/// đứng ở cổng POC và sắp bấm duyệt dựa trên lời đã rút. Cùng kỷ luật với chiều đi — chỉ ghi nhận + báo,
/// KHÔNG đụng vào pipeline: các bước đã chạy rồi thì không lùi được bằng một cú bấm ở đây.
/// </para>
/// </summary>
public class WithdrawPocAcceptanceUseCase
{
    private readonly AppDbContext _db;
    private readonly INotificationService _notifications;

    public WithdrawPocAcceptanceUseCase(AppDbContext db, INotificationService notifications)
    {
        _db = db;
        _notifications = notifications;
    }

    public async Task<WithdrawPocAcceptanceResult> ExecuteAsync(Guid projectId, string withdrawnBy, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return WithdrawPocAcceptanceResult.ProjectNotFound;

        if (!project.PocAcceptedAtUtc.HasValue)
            return WithdrawPocAcceptanceResult.NotAccepted;

        var acceptedBy = project.PocAcceptedBy;
        project.PocAcceptedAtUtc = null;
        project.PocAcceptedBy = null;

        // Cùng run mà chiều đi đã báo — thông báo bám theo delivery run để chuông/email dẫn về đúng cổng.
        var deliveryRun = await _db.WorkflowRuns
            .Where(r => r.ProjectId == projectId && r.Name.StartsWith("Delivery Workflow"))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (deliveryRun != null)
        {
            var actor = string.IsNullOrWhiteSpace(withdrawnBy) ? (acceptedBy ?? "Người yêu cầu") : withdrawnBy.Trim();
            await _notifications.NotifyPocAcceptanceWithdrawnAsync(deliveryRun, actor, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return WithdrawPocAcceptanceResult.Ok;
    }
}
