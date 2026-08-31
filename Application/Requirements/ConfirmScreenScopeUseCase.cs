using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// CHỐT bảng màn hình: người dùng bỏ tích màn hình không cần, sửa việc của từng màn rồi gửi. Lưu vào
/// <see cref="ICOGenerator.Domain.Project.ScreenScopeMap"/>.
///
/// <para>
/// Đây là bảng mà bảng phân quyền sẽ đứng lên: <see cref="PermissionMatrixGate.EffectiveScreens"/> đọc nó
/// làm nguồn DÒNG. Trước khi có bước này, toàn bộ phần phân quyền — thứ đã được dựng cẩn thận để có bằng
/// chứng trên từng ô — lại đứng trên một danh sách màn hình do LLM chắt mà người dùng chưa bao giờ nhìn
/// thấy để phản đối.
/// </para>
///
/// <para>
/// Đây cũng là ĐÚNG MỘT chỗ đóng dấu <see cref="ICOGenerator.Contracts.Requirements.ScreenScopeRow.ConfirmedByUser"/>
/// (<see cref="ScreenScopeMapBuilder.Sanitize"/> làm việc đó), tức chỗ duy nhất một mục phạm vi rời khỏi
/// trạng thái CHỜ DUYỆT. Mọi đường khác — lượt chắt lọc, hai bảng gieo màn hình sang — chỉ được THÊM mục
/// chờ duyệt vào bảng.
/// </para>
///
/// <para>
/// Cùng khuôn HAI BƯỚC với <see cref="ConfirmPermissionMatrixUseCase"/>: chỉ lưu, không gọi LLM; tin nhắn
/// kể lại do server soạn từ bảng đã chuẩn hoá rồi trình duyệt gửi qua đúng đường chat thường.
/// </para>
/// </summary>
public class ConfirmScreenScopeUseCase
{
    private readonly AppDbContext _db;

    public ConfirmScreenScopeUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Số màn hình đã lưu + tin nhắn mà trình duyệt phải gửi tiếp vào khung chat.</summary>
    public sealed record Result(int Rows, string Message);

    public async Task<Result> ExecuteAsync(Guid projectId, string? screensJson, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return new Result(0, string.Empty);

        // Payload rỗng/hỏng ⇒ không ghi gì và báo 0 dòng để UI GIỮ bảng lại, cùng luật với bảng phân quyền.
        // Phải chặn TRƯỚC Sanitize: Sanitize bù mọi màn hình còn thiếu thành dòng trắng — đúng việc của nó
        // với một bảng gửi thiếu vài dòng, nhưng với payload rỗng thì nó biến "không đọc được gì" thành một
        // bảng trắng đầy đủ được lưu đè, kèm tin nhắn "mình đã rà bảng màn hình" liệt kê tên suông.
        var submitted = ScreenScopeMapBuilder.Parse(screensJson);
        if (submitted.Count == 0)
            return new Result(0, string.Empty);

        // Tên màn hình phải khớp lại BẢNG SERVER ĐÃ RENDER — server không tin payload nó vừa render ra,
        // nhưng cũng không được đối chiếu với một danh sách đã đổi dưới chân người dùng. Xem
        // AllowedScreensAsync.
        var allowedScreens = await AllowedScreensAsync(projectId, project.ScreenScopeMap, cancellationToken);
        var rows = ScreenScopeMapBuilder.Sanitize(submitted, allowedScreens);
        if (rows.Count == 0)
            return new Result(0, string.Empty);

        // KHÔNG dòng nào được giữ ⇒ KHÔNG ghi gì, và UI giữ bảng lại (0 dòng) như với payload hỏng. Bảng
        // này là nguồn phạm vi DUY NHẤT nên không còn danh sách nào để rơi về: lưu một bảng trắng trơn là
        // khóa chết cổng phân quyền — nó đòi phạm vi có mục mới mở — và khóa trong im lặng, không có gì
        // trên màn hình nói vì sao. Một ứng dụng không có màn hình nào cũng không phải thứ dựng được.
        if (!rows.Any(r => r.Included))
            return new Result(0, string.Empty);

        // GIỮ phần bảng vừa bày KHÔNG mang ra hỏi — các dòng/chức năng người dùng đã bỏ tích ở lần chốt
        // trước. Chúng là BIA: mất chúng thì lượt chắt lọc kế tiếp gặp lại đúng cái tên ấy trong hội thoại
        // sẽ ghép lại vào bảng như một mục mới tinh, tức mở lại thứ người dùng vừa đóng. Xem MergeConfirmed.
        var merged = ScreenScopeMapBuilder.MergeConfirmed(project.ScreenScopeMap, rows);
        project.ScreenScopeMap = JsonSerializer.Serialize(merged);

        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, ScreenScopeMapBuilder.RenderUserMessage(rows));
    }

    /// <summary>
    /// Danh sách màn hình mà payload được phép khớp: tên của CHÍNH BẢNG SERVER ĐÃ RENDER, đọc lại từ lượt BA
    /// bày bảng (<c>AgentConversation.ScreenScopeMap</c> — cùng lượt mà view dùng để dựng lại panel sau F5).
    ///
    /// <para>
    /// Vì sao không đọc lại BẢNG ĐANG LƯU tại đây, dù chính nó là nguồn DÒNG lúc bày bảng: giữa lúc bày và
    /// lúc bấm gửi vẫn có thể có một lượt chat khác, và lượt chắt lọc chạy ở hậu kỳ lượt đó
    /// (<c>RequirementsController</c> gọi <c>UpdateInterviewOutlookAsync</c> sau frame done) ghép thêm
    /// được mục mới vào bảng. Đối chiếu với một danh sách đã dài ra dưới chân người dùng thì các mục mới ấy
    /// được "bù" vào bản chốt ở dạng TRẮNG — không việc, không chức năng, không bước luồng — và bị đóng dấu
    /// đã-duyệt trong khi họ chưa từng nhìn thấy. Danh sách đúng luôn là đúng thứ đang hiện trên màn hình.
    /// </para>
    ///
    /// <para>
    /// Không có lượt bảng nào (dự án cũ chốt bằng đường khác, hoặc lượt đã bị "New Chat" lưu trữ) ⇒ quay về
    /// các dòng còn tích của bảng đang lưu. Fail-open: mất chốt chặn tên màn hình rẻ hơn nhiều so với một
    /// nút gửi không bao giờ lưu được gì.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> AllowedScreensAsync(
        Guid projectId, string? screenScopeJson, CancellationToken cancellationToken)
    {
        var rendered = await _db.AgentConversations
            .AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.ArchivedAt == null
                && c.Role == "assistant" && c.ScreenScopeMap != null)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Select(c => c.ScreenScopeMap)
            .FirstOrDefaultAsync(cancellationToken);

        var screens = ScreenScopeMapBuilder.Parse(rendered).Select(r => r.Screen).ToList();
        return screens.Count > 0 ? screens : ScreenScopeMapBuilder.EffectiveScreens(screenScopeJson);
    }
}
