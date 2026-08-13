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
/// làm nguồn DÒNG thay cho <c>Project.PlannedScope</c> thô. Trước khi có bước này, toàn bộ phần phân quyền
/// — thứ đã được dựng cẩn thận để có bằng chứng trên từng ô — lại đứng trên một danh sách màn hình do LLM
/// chắt mà người dùng chưa bao giờ nhìn thấy để phản đối.
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
        var allowedScreens = await AllowedScreensAsync(projectId, project.PlannedScope, cancellationToken);
        var rows = ScreenScopeMapBuilder.Sanitize(submitted, allowedScreens);
        if (rows.Count == 0)
            return new Result(0, string.Empty);

        project.ScreenScopeMap = JsonSerializer.Serialize(rows);

        // GHI NGƯỢC PHẠM VI. Người dùng vừa tự tay duyệt danh sách màn hình, nên nó thay cho bản LLM đoán:
        // lượt chắt lọc kế tiếp nhận bản đã duyệt làm gốc để gộp tiếp thay vì diễn đạt lại từ đầu, và
        // EffectiveScreens không còn phải bù vào những mục chỉ khác CHỮ so với dòng người dùng vừa giữ —
        // nếu không, bảng phân quyền ngay sau đó mọc thêm một loạt dòng trùng nghĩa mà không dòng nào có
        // việc của màn hình. Đây cũng là chỗ vá điều mà EffectiveScreens từ trước tới nay chỉ chắn được
        // một nửa: lượt chắt lọc không đọc bảng nên nó giữ mãi cả mục người dùng đã bỏ tích.
        //
        // KHÔNG dòng nào được giữ ⇒ để nguyên PlannedScope. Ghi null vào đây là cắt luôn đường fail-open
        // của EffectiveScreens (nó quay về PlannedScope khi bảng trống) và khóa chết cổng phân quyền trong
        // im lặng.
        var kept = rows.Where(r => r.Included).Select(r => r.Screen).ToList();
        if (kept.Count > 0)
            project.PlannedScope = InterviewOutlookService.Store(kept);

        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, ScreenScopeMapBuilder.RenderUserMessage(rows));
    }

    /// <summary>
    /// Danh sách màn hình mà payload được phép khớp: tên của CHÍNH BẢNG SERVER ĐÃ RENDER, đọc lại từ lượt BA
    /// bày bảng (<c>AgentConversation.ScreenScopeMap</c> — cùng lượt mà view dùng để dựng lại panel sau F5).
    ///
    /// <para>
    /// Vì sao không dùng <c>Project.PlannedScope</c> đọc lại tại đây, dù chính nó là nguồn DÒNG lúc bày
    /// bảng: lượt chắt lọc "triển vọng phỏng vấn" chạy ở HẬU KỲ ngay lượt bày bảng
    /// (<c>RequirementsController</c> gọi <c>UpdateInterviewOutlookAsync</c> sau frame done) và nó GHI ĐÈ
    /// cả danh sách. Nghĩa là bảng vừa hiện trên màn hình đã dựng từ một bản <c>PlannedScope</c> không còn
    /// tồn tại nữa. Chỉ cần lượt chắt lọc diễn đạt lại một mục — "…trong nhà máy" thành "…theo orgUnit" —
    /// là <c>MatchScreen</c> trượt, dòng người dùng vừa điền bị bỏ, và các mục phạm vi mới bù vào chỗ đó ở
    /// dạng TRẮNG: họ gửi một bảng đã rà và nhận lại một danh sách tên suông, không việc, không chức năng,
    /// không bước luồng — trong khi khối ngữ cảnh của bảng đã chốt lại cấm BA hỏi lại việc của từng màn.
    /// Đây không phải tình huống hiếm: lượt chắt lọc LUÔN chạy giữa lúc bày bảng và lúc bấm gửi.
    /// </para>
    ///
    /// <para>
    /// Không có lượt bảng nào (dự án cũ chốt bằng đường khác, hoặc lượt đã bị "New Chat" lưu trữ) ⇒ quay về
    /// <paramref name="plannedScope"/> như trước. Fail-open: mất chốt chặn tên màn hình rẻ hơn nhiều so với
    /// một nút gửi không bao giờ lưu được gì.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> AllowedScreensAsync(
        Guid projectId, string? plannedScope, CancellationToken cancellationToken)
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
        return screens.Count > 0 ? screens : InterviewOutlookService.ParseItems(plannedScope);
    }
}
