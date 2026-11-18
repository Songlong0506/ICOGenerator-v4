using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Cổng TẤT ĐỊNH quyết định "đã tới lúc BA soạn bảng phân quyền chưa" — suy từ chính bản đồ bao phủ, không
/// hỏi LLM. Nhóm «Phân quyền theo nghiệp vụ» được hoãn lại tới CUỐI buổi phỏng vấn, và đây là thứ biết khi
/// nào "cuối buổi" đã tới. Tới lúc đó <see cref="PermissionMatrixDraftService"/> soạn bảng ngầm ở đầu lượt
/// và lưu thẳng; người dùng xem/sửa nó ở popup mở từ dòng «Phân quyền theo nghiệp vụ» của panel tiến độ.
///
/// <para>
/// <b>Vì sao nhóm này KHÔNG được hỏi bằng câu hỏi.</b> Câu *"mỗi vai trò được xem và làm những gì?"* bắt
/// người dùng nghiệp vụ tự dựng cả ma trận (màn hình × chức năng × vai trò) trong đầu; câu trả lời thật gần
/// như luôn là *"cứ vậy đã, có gì tôi bổ sung sau"*, rồi BA tự soạn phương án và một chip "Đồng ý" đóng
/// dấu [RÕ] cho cả nhóm. Phần quyết định thật của nhóm — vai nào làm bước nào, ai quản lý danh mục nào —
/// đã được hỏi ở nhóm «Chức năng &amp; luồng nghiệp vụ chính» và «Dữ liệu / danh mục chính»; phần còn lại
/// (CRUD trên từng chức năng của từng màn) suy được từ đó, nên BA tự soạn và người dùng sửa khi cần thay vì
/// bị hỏi lại. Trong lúc cổng chưa mở, ngữ cảnh chat mang một lệnh cấm hỏi lẻ.
/// </para>
///
/// <para>
/// Điều kiện mở, cả bốn đều bắt buộc:
/// </para>
/// <list type="number">
///   <item><b>Chưa có bảng</b> — có rồi thì nhóm đã có câu trả lời, không soạn lại (người dùng sửa ở popup).</item>
///   <item><b>Phạm vi đã có</b> — các DÒNG của bảng chính là các màn hình CÒN TÍCH của bảng màn hình
///   (<see cref="ScreenScopeMapBuilder.EffectiveScreens"/>). Phạm vi trống thì bảng không có gì để soạn.</item>
///   <item><b>Phạm vi đã CHỐT</b> (bản nhận <see cref="Project"/>): bảng màn hình không còn mục nào chưa
///   soạn xong. Soạn quyền trên một phạm vi còn đang trôi là thiếu đúng các chức năng sắp được thêm.</item>
///   <item><b>Mọi nhóm áp dụng KHÁC đã [RÕ]</b> — đây là định nghĩa "cuối buổi".</item>
/// </list>
///
/// <para>
/// Lưu ý về vòng lặp: <see cref="RequirementReadinessGate"/> đòi MỌI dòng áp dụng <c>[RÕ]</c> mới mở nút
/// "Write Requirement", mà nhóm phân quyền chỉ lên <c>[RÕ]</c> khi bảng đã có. Nên cổng này cố tình BỎ QUA
/// đúng dòng phân quyền khi xét — nếu không, hai cổng khóa lẫn nhau và không cổng nào mở được.
/// </para>
///
/// <para>
/// <b>Nhóm «Thông báo / nhắc nhở» cũng được bỏ qua</b> (xem <see cref="IsDeferredGroup"/>): nó không
/// chốt ở buổi phỏng vấn và không bao giờ được hỏi, nên không phải thứ bảng phân quyền phải chờ.
/// <see cref="CoverageConfirmedTableGuard"/> vốn đặt nó về trạng thái không chặn ở mọi lượt distill; bỏ qua
/// ở đây là phanh phụ cho bản đồ lưu từ trước lượt distill đầu tiên sau khi bỏ bảng thông báo.
/// </para>
/// </summary>
public static class PermissionMatrixGate
{
    /// <summary>Đã tới lúc soạn bảng cho dự án này chưa. Xem điều kiện ở ghi chú class.</summary>
    public static bool ShouldDraft(Project project)
        => !ScreenScopeMapBuilder.HasPending(project.ScreenScopeMap)
           && ShouldDraft(project.RequirementCoverageMap, project.PermissionMatrix, EffectiveScreens(project));

    /// <summary>
    /// Các DÒNG của bảng phân quyền: mọi màn hình CÒN TÍCH của bảng màn hình — xem
    /// <see cref="ScreenScopeMapBuilder.EffectiveScreens"/>. Đây là nguồn phạm vi DUY NHẤT của dự án.
    /// </summary>
    public static IReadOnlyList<string> EffectiveScreens(Project project)
        => ScreenScopeMapBuilder.EffectiveScreens(project.ScreenScopeMap);

    /// <summary>
    /// Bản thuần dữ liệu của <see cref="ShouldDraft(Project)"/> — để test và để gọi từ nơi không có entity.
    /// KHÔNG gồm vế "phạm vi đã chốt".
    /// </summary>
    public static bool ShouldDraft(string? coverageMap, string? permissionMatrixJson, IReadOnlyList<string> screens)
    {
        if (IsConfirmed(permissionMatrixJson))
            return false;
        if (screens.Count == 0)
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        // Các nhóm KHÁC hai nhóm không-hỏi (phân quyền, thông báo): còn dòng áp dụng nào chưa [RÕ] thì buổi phỏng vấn chưa
        // tới cuối.
        var others = items.Where(x => !IsDeferredGroup(x)).ToList();
        if (others.Count == 0)
            return false;
        if (others.Any(x => x.Status is CoverageStatus.Partial or CoverageStatus.NotAsked))
            return false;

        // Bản đồ toàn [KHÔNG ÁP DỤNG] là bản đồ hỏng, không phải dự án đã rõ — cùng luật fail-closed với
        // RequirementReadinessGate.
        return others.Any(x => x.Status == CoverageStatus.Clear);
    }

    /// <summary>Dự án này đã có bảng phân quyền chưa (BA đã soạn, hoặc người dùng đã lưu ở popup).</summary>
    public static bool IsConfirmed(string? permissionMatrixJson)
        => PermissionMatrixBuilder.Parse(permissionMatrixJson).Count > 0;

    /// <summary>
    /// Hai nhóm không đi đường câu hỏi: phân quyền (HOÃN tới cuối buổi, <c>[RÕ]</c> khi bảng đã có) và
    /// thông báo (không chốt ở buổi phỏng vấn) — xem ghi chú class. Nhận ra bằng KHOÁ nhóm (<see cref="CoverageGroupKey"/>), không bằng nhãn.
    /// </summary>
    private static bool IsDeferredGroup(CoverageMapItem item)
        => item.Key is CoverageGroupKey.Permissions or CoverageGroupKey.Notifications;
}
