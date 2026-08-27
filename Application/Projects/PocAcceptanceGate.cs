using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>
/// KHOÁ SAU NGHIỆM THU — một chốt duy nhất trả lời câu hỏi "project này có đang bị đóng băng nội dung
/// không?", dùng chung cho mọi đường ghi bị khoá.
///
/// <para>
/// Nghiệm thu bản demo (<see cref="AcceptPocUseCase"/>) nay là một CÔNG TẮC HAI CHIỀU: bấm "Approve POC"
/// là chốt lại nội dung đang có — chat BA ở màn hình Requirement và ghi chú ở trang POC Review đều dừng
/// nhận thay đổi; muốn sửa tiếp thì bấm "Withdraw Approve" (xem <see cref="WithdrawPocAcceptanceUseCase"/>).
/// Lý do có lớp này thay vì vài dòng <c>if</c> rải trong controller: khoá chỉ ở giao diện là khoá GIẢ —
/// ghi chú POC còn một cửa thứ hai qua link chia sẻ (<c>PocShareController</c>, ẩn danh) và mọi endpoint
/// đều gọi được thẳng. Chốt nằm ở tầng use case thì mọi cửa đi qua cùng một luật.
/// </para>
/// </summary>
public class PocAcceptanceGate
{
    /// <summary>Câu giải thích dùng chung cho mọi lỗi 4xx do khoá này sinh ra — người dùng cần biết chỗ mở khoá.</summary>
    public const string LockedMessage =
        "Bản demo đã được nghiệm thu nên nội dung đang khoá. Bấm \"Withdraw Approve\" trên trang POC Review để mở khoá rồi chỉnh sửa tiếp.";

    private readonly AppDbContext _db;

    public PocAcceptanceGate(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>true khi project đã nghiệm thu ⇒ mọi đường ghi vào nội dung yêu cầu/ghi chú phải từ chối.</summary>
    public Task<bool> IsLockedAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _db.Projects.AsNoTracking()
            .AnyAsync(p => p.Id == projectId && p.PocAcceptedAtUtc != null, cancellationToken);
}
