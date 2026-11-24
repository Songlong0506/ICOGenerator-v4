using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Cổng TẤT ĐỊNH quyết định BA soạn bảng phân quyền LẦN ĐẦU hay chỉ BỔ SUNG dòng — đọc thuần trên bản thiết kế
/// UX và bảng đang lưu, không hỏi LLM. Lượt soạn (<see cref="PermissionMatrixDraftService"/>) chạy ở bước "Write
/// Requirement" (<see cref="DesignPreparationService"/>) ngay sau lượt thiết kế UX; người dùng xem/sửa bảng ở
/// popup mở từ nút dưới panel tiến độ.
///
/// <para>
/// <b>Vì sao phân quyền KHÔNG được hỏi bằng câu hỏi, và không là một nhóm của bản đồ bao phủ.</b> Câu
/// *"mỗi vai trò được xem và làm những gì?"* bắt người dùng nghiệp vụ tự dựng cả ma trận (màn hình × chức
/// năng × vai trò) trong đầu; câu trả lời thật gần như luôn là *"cứ vậy đã, có gì tôi bổ sung sau"*. Phần
/// quyết định thật — vai nào làm bước nào, ai quản lý danh mục nào — đã được hỏi ở nhóm «Chức năng &amp; luồng
/// nghiệp vụ chính» và «Dữ liệu / danh mục chính»; phần còn lại (CRUD trên từng tác vụ) suy được từ đó, nên BA
/// tự soạn và người dùng sửa khi cần. Một nhóm «Phân quyền» trên bản đồ chỉ còn mang câu máy tự viết ("đã có
/// bảng…") mà lại chặn nút "Write Requirement" cho tới khi bảng — vốn cần bản thiết kế — có mặt.
/// </para>
/// </summary>
public static class PermissionMatrixGate
{
    /// <summary>
    /// Đã tới lúc soạn bảng LẦN ĐẦU chưa: chưa có bảng, đã có bản thiết kế UX (các DÒNG của bảng chính là các TÁC
    /// VỤ của bản thiết kế) và bản thiết kế không còn tác vụ chưa xếp chỗ.
    /// </summary>
    public static bool ShouldDraft(Project project)
        => !IsConfirmed(project.PermissionMatrix)
           && UxBlueprintBuilder.IsDesigned(project.UxBlueprint)
           && !UxBlueprintBuilder.HasPending(project.UxBlueprint)
           && UxBlueprintBuilder.Parse(project.UxBlueprint).Tasks.Count > 0;

    /// <summary>
    /// Tác vụ của bản thiết kế CHƯA có dòng trong bảng đang lưu — phạm vi lộ ra SAU lần soạn đầu. Lượt soạn
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
}
