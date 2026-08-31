using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// CHỐT bảng báo cáo / thống kê: người dùng bỏ tích các báo cáo không cần, sửa lại câu hỏi mỗi báo cáo trả
/// lời, thêm dòng còn thiếu rồi gửi. Lưu vào <see cref="ICOGenerator.Domain.Project.ReportMap"/> — từ đó
/// khối "đã chốt" đi vào ngữ cảnh chat, vào lượt distill bản đồ bao phủ và vào prompt sinh AI Design Spec.
///
/// <para>
/// Cùng khuôn HAI BƯỚC với <see cref="ConfirmEntityMapUseCase"/>: chỉ lưu, không gọi LLM; tin nhắn kể lại
/// do server soạn từ bảng đã chuẩn hoá rồi trình duyệt gửi qua đúng đường chat thường.
/// </para>
///
/// <para>
/// <b>GIEO MÀN HÌNH BÁO CÁO VÀO PHẠM VI — đây mới là chỗ bảng trả tiền cho chính nó.</b> Một báo cáo là
/// một chỗ người dùng mở ra và nhìn thấy, tức một màn hình; nằm lại trong cột <c>ReportMap</c> thì nó không
/// có DÒNG nào trong bảng phân quyền và không có mục nào ở <c>## 6. Screens To Generate</c> — mặc nhiên
/// "không ai được xem" một màn hình người dùng vừa đặt hàng. Đường ra là các DÒNG của bảng màn hình, đúng
/// như màn hình danh mục của <see cref="ConfirmEntityMapUseCase"/>: không cổng nào phải sửa, vì bảng MÀN
/// HÌNH đứng SAU bảng này trong thứ tự phụ thuộc (<c>luồng → đối tượng → báo cáo → màn hình</c>) nên các
/// mục vừa gieo có mặt ngay ở lần bày ĐẦU của nó, rồi đi tiếp thành DÒNG của bảng phân quyền.
/// </para>
///
/// <para>
/// Chỉ THÊM, không đụng tới dòng nào đang có (<c>ScreenScopeMapBuilder.Merge</c>): bảng màn hình có thể đã
/// được người dùng tự tay rà — ca đó hiếm sau khi thứ tự được sửa, nhưng vẫn tới được qua đường MỞ LẠI của
/// <c>ScreenScopeGate</c> khi nhóm «Báo cáo / thống kê» chỉ lên <c>[RÕ]</c> sau lúc bảng màn hình đã chốt.
/// Mục trùng, và mục trùng một dòng họ đã bỏ tích, đều bị bỏ.
/// </para>
/// </summary>
public class ConfirmReportMapUseCase
{
    private readonly AppDbContext _db;

    public ConfirmReportMapUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Số báo cáo đã lưu + tin nhắn mà trình duyệt phải gửi tiếp vào khung chat.</summary>
    public sealed record Result(int Rows, string Message);

    public async Task<Result> ExecuteAsync(Guid projectId, string? reportsJson, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return new Result(0, string.Empty);

        // Bộ đối chiếu ô "lấy số từ" đọc lại từ DB chứ không tin payload: bảng có thể đã nằm trên màn hình
        // từ trước lượt người dùng chốt lại bảng đối tượng, và một nguồn trỏ vào đối tượng vừa bị bỏ tích
        // là một mối nối gãy mà không tầng nào phía sau soát được.
        var rows = ReportMapBuilder.Sanitize(
            ReportMapBuilder.Parse(reportsJson), EntityMapBuilder.EntityNames(project.EntityMap));
        if (rows.Count == 0)
            return new Result(0, string.Empty);

        project.ReportMap = JsonSerializer.Serialize(rows);

        // Cùng đường gieo với màn hình danh mục của ConfirmEntityMapUseCase — xem ghi chú ở đó.
        var merged = ScreenScopeMapBuilder.Merge(
            project.ScreenScopeMap,
            ReportMapBuilder.ReportScreens(rows).Select(s => new ScopeAddition { Screen = s }));
        if (merged != null)
            project.ScreenScopeMap = JsonSerializer.Serialize(merged);

        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, ReportMapBuilder.RenderUserMessage(rows));
    }
}
