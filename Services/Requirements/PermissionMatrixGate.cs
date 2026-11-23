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
///   <item><b>Đã có bản thiết kế UX</b> — các DÒNG của bảng chính là các TÁC VỤ của bản thiết kế. Chưa thiết
///   kế thì bảng không có gì để soạn.</item>
///   <item><b>Bản thiết kế không còn tác vụ chưa xếp chỗ</b> (bản nhận <see cref="Project"/>): soạn quyền trên
///   một phạm vi còn đang trôi là thiếu đúng các tác vụ sắp được thêm.</item>
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
    /// <summary>Đã tới lúc soạn bảng LẦN ĐẦU chưa.</summary>
    public static bool ShouldDraft(Project project)
        => UxBlueprintBuilder.IsDesigned(project.UxBlueprint)
           && !UxBlueprintBuilder.HasPending(project.UxBlueprint)
           && ShouldDraft(project.RequirementCoverageMap, project.PermissionMatrix, UxBlueprintBuilder.Parse(project.UxBlueprint).Tasks.Count);

    public static bool ShouldDraft(string? coverageMap, string? permissionMatrixJson, int taskCount)
    {
        if (IsConfirmed(permissionMatrixJson))
            return false;
        if (taskCount == 0)
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

    /// <summary>
    /// Tác vụ của bản thiết kế CHƯA có dòng trong bảng đang lưu — phạm vi trôi SAU lần soạn đầu. Lượt soạn
    /// phân quyền bổ sung đúng các dòng này (không soạn lại cả bảng: dòng người dùng đã sửa ở popup giữ nguyên).
    /// Rỗng khi chưa có bảng, chưa có bản thiết kế, hoặc bản thiết kế còn tác vụ chưa xếp chỗ.
    /// </summary>
    public static List<UxTask> MissingTasks(Project project)
    {
        if (!IsConfirmed(project.PermissionMatrix) || UxBlueprintBuilder.HasPending(project.UxBlueprint))
            return new List<UxTask>();

        var rows = PermissionMatrixBuilder.Parse(project.PermissionMatrix);
        return UxBlueprintBuilder.Parse(project.UxBlueprint).Tasks
            .Where(t => !rows.Any(r => UxBlueprintBuilder.SameId(r.TaskId, t.Id)))
            .ToList();
    }

    public static bool IsConfirmed(string? permissionMatrixJson)
        => PermissionMatrixBuilder.Parse(permissionMatrixJson).Count > 0;

    private static bool IsDeferredGroup(CoverageMapItem item)
        => item.Key is CoverageGroupKey.Permissions or CoverageGroupKey.Notifications;
}
