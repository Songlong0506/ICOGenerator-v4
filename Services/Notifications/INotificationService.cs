using ICOGenerator.Domain;

namespace ICOGenerator.Services.Notifications;

/// <summary>
/// Ghi thông báo in-app khi một <see cref="WorkflowRun"/> chuyển trạng thái đáng chú ý. Người nhận là
/// những user đang hoạt động CÓ quyền duyệt delivery (<c>DeliveryAdvance</c>) — họ mới là người cần
/// hành động ở cổng duyệt. Đây là "seam" trung tâm: kênh ngoài (Teams/email) sau này cắm vào đây.
///
/// Hợp đồng: các method chỉ <c>Add</c> entity vào DbContext hiện hành và KHÔNG gọi SaveChanges — người
/// gọi (worker) lưu atomic cùng lần chuyển trạng thái. Mọi lỗi được nuốt (fail-open): một thông báo hỏng
/// KHÔNG bao giờ làm gãy workflow.
/// </summary>
public interface INotificationService
{
    /// <summary>Một bước đã xong, run đang chờ người duyệt để sang <paramref name="nextStepTitle"/>.</summary>
    Task NotifyGateOpenedAsync(WorkflowRun run, string nextStepTitle, CancellationToken cancellationToken = default);

    /// <summary>Cả workflow giao hàng đã hoàn tất.</summary>
    Task NotifyRunCompletedAsync(WorkflowRun run, CancellationToken cancellationToken = default);

    /// <summary>Workflow dừng vì lỗi — cần người xem lại.</summary>
    Task NotifyRunFailedAsync(WorkflowRun run, string? error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Người yêu cầu đã NGHIỆM THU bản demo (POC). Đây là tín hiệu đội delivery vẫn phải đi hỏi miệng
    /// trước đây: cổng duyệt nằm trên Agent Dashboard nên không ai biết người xem demo đã ưng hay chưa.
    /// </summary>
    Task NotifyPocAcceptedAsync(WorkflowRun run, string acceptedBy, CancellationToken cancellationToken = default);

    /// <summary>Người yêu cầu rút lại lời nghiệm thu — đội delivery phải biết trước khi bấm duyệt cổng POC.</summary>
    Task NotifyPocAcceptanceWithdrawnAsync(WorkflowRun run, string withdrawnBy, CancellationToken cancellationToken = default);
}
