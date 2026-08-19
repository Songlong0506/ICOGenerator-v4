using System.Text.Json;
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
/// "không ai được xem" một màn hình người dùng vừa đặt hàng. Đường ra là <c>Project.PlannedScope</c>, đúng
/// như màn hình danh mục của <see cref="ConfirmEntityMapUseCase"/>: không cổng nào phải sửa, vì
/// <c>ScreenScopeGate</c> đã có sẵn đường MỞ LẠI bảng màn hình khi phạm vi trôi sau lúc chốt, và bảng phân
/// quyền (đứng SAU bảng này trong thứ tự phụ thuộc) lấy dòng từ chính phạm vi đó.
/// </para>
///
/// <para>
/// GHÉP THÊM chứ không ghi đè, và giữ nguyên thứ tự cũ: <c>PlannedScope</c> là danh sách người dùng đã rà ở
/// bảng màn hình (<c>ConfirmScreenScopeUseCase</c> ghi ngược lên đây), nên thay nó bằng mấy dòng báo cáo là
/// xoá sạch phạm vi đã duyệt. Mục trùng bị bỏ.
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

        var scope = InterviewOutlookService.ParseItems(project.PlannedScope).ToList();
        var known = new HashSet<string>(scope.Select(NormalizeScope), StringComparer.Ordinal);
        var added = ReportMapBuilder.ReportScreens(rows).Where(s => known.Add(NormalizeScope(s))).ToList();
        if (added.Count > 0)
        {
            scope.AddRange(added);
            project.PlannedScope = InterviewOutlookService.Store(scope);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, ReportMapBuilder.RenderUserMessage(rows));
    }

    /// <summary>
    /// Khoá so trùng của một mục phạm vi — chép đúng phép chuẩn hoá mà <c>ScreenScopeMapBuilder</c> dùng để
    /// nhận ra "màn hình mới", để một mục vừa gieo không quay lại thành mục mới ở lượt sau chỉ vì khác hoa
    /// thường hay khác một dấu câu ở cuối.
    /// </summary>
    private static string NormalizeScope(string value)
        => string.Join(' ', (value ?? string.Empty).ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim(' ', '.', ',', ':', ';', '-', '–');
}
