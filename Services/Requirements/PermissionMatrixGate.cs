using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Cổng TẤT ĐỊNH quyết định "đã tới lúc bày bảng phân quyền chưa" — suy từ chính bản đồ bao phủ, không hỏi
/// LLM. Nhóm «Phân quyền theo nghiệp vụ» được hoãn lại tới CUỐI buổi phỏng vấn, và đây là thứ biết khi nào
/// "cuối buổi" đã tới.
///
/// <para>
/// Vì sao phải là cơ chế chứ không phải một câu dặn trong prompt: bản đồ bao phủ là la bàn chọn câu hỏi kế
/// tiếp, và nó ghi nhóm phân quyền là <c>[CHƯA HỎI]</c> suốt cả buổi. Prompt có dặn "để cuối" thì model
/// vẫn thấy một nhóm chưa hỏi nằm đó và sớm muộn cũng hỏi — rồi nhận về "cứ vậy đã, có gì tôi bổ sung sau",
/// tự soạn phương án, và một chip "Đồng ý" đóng dấu [RÕ] cho cả nhóm. Cổng này đảo thứ tự bằng máy:
/// trong lúc chưa mở, ngữ cảnh chat mang một lệnh cấm hỏi lẻ; lúc mở, lượt chat được thay bằng lượt bày bảng.
/// </para>
///
/// <para>
/// Điều kiện mở, cả ba đều bắt buộc:
/// </para>
/// <list type="number">
///   <item><b>Chưa chốt bảng nào</b> — chốt rồi thì nhóm đã có câu trả lời thật, không bày lại.</item>
///   <item><b>Phạm vi đã có</b> — các DÒNG của bảng chính là màn hình đã chắt ra từ hội thoại
///   (<c>Project.PlannedScope</c>). Phạm vi trống thì bảng không có gì để hỏi.</item>
///   <item><b>Mọi nhóm áp dụng KHÁC đã [RÕ]</b> — đây là định nghĩa "cuối buổi". Hỏi sớm hơn thì bảng
///   thiếu nửa số màn hình, mà quyền của một màn hình chưa tồn tại thì không ai trả lời được.</item>
/// </list>
///
/// <para>
/// Lưu ý về vòng lặp: <see cref="RequirementReadinessGate"/> đòi MỌI dòng áp dụng <c>[RÕ]</c> mới mở nút
/// "Write Requirement", mà nhóm phân quyền chỉ lên <c>[RÕ]</c> sau khi bảng được chốt. Nên cổng này cố tình
/// BỎ QUA đúng dòng phân quyền khi xét — nếu không, hai cổng khóa lẫn nhau và không cổng nào mở được.
/// </para>
/// </summary>
public static class PermissionMatrixGate
{
    /// <summary>
    /// Nhãn nhóm phân quyền trong bản đồ bao phủ — khớp <c>requirement-coverage.v3.md</c> và mục 12 nhãn
    /// nhóm của <c>requirement-chat.v4.md</c>. So khớp bằng tiền tố "Phân quyền" để một lượt distill viết
    /// chệch phần đuôi không làm cổng câm.
    /// </summary>
    public const string PermissionGroupLabel = "Phân quyền theo nghiệp vụ";

    /// <summary>Đã tới lúc bày bảng cho dự án này chưa. Xem điều kiện ở ghi chú class.</summary>
    public static bool ShouldAsk(Project project)
        => ShouldAsk(project.RequirementCoverageMap, project.PermissionMatrix, EffectiveScreens(project));

    /// <summary>
    /// Các DÒNG của bảng phân quyền. Bảng màn hình đã chốt là nguồn ưu tiên — nó chính là
    /// <c>Project.PlannedScope</c> sau khi người dùng đã tự tay rà (xem
    /// <see cref="ScreenScopeMapBuilder.EffectiveScreens"/>). Chưa chốt ⇒ về đúng hành vi cũ: phạm vi thô
    /// do LLM chắt.
    /// </summary>
    public static IReadOnlyList<string> EffectiveScreens(Project project)
        => ScreenScopeMapBuilder.EffectiveScreens(
            project.ScreenScopeMap, InterviewOutlookService.ParseItems(project.PlannedScope));

    /// <summary>Bản thuần dữ liệu của <see cref="ShouldAsk(Project)"/> — để test và để gọi từ nơi không có entity.</summary>
    public static bool ShouldAsk(string? coverageMap, string? permissionMatrixJson, IReadOnlyList<string> plannedScope)
    {
        if (IsConfirmed(permissionMatrixJson))
            return false;
        if (plannedScope.Count == 0)
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        // Các nhóm KHÁC nhóm phân quyền: còn dòng áp dụng nào chưa [RÕ] thì buổi phỏng vấn chưa tới cuối.
        var others = items.Where(x => !IsPermissionGroup(x)).ToList();
        if (others.Count == 0)
            return false;
        if (others.Any(x => x.Status is "MỘT PHẦN" or "CHƯA HỎI"))
            return false;

        // Bản đồ toàn [KHÔNG ÁP DỤNG] là bản đồ hỏng, không phải dự án đã rõ — cùng luật fail-closed với
        // RequirementReadinessGate.
        return others.Any(x => x.Status == "RÕ");
    }

    /// <summary>Dự án này đã chốt bảng phân quyền chưa.</summary>
    public static bool IsConfirmed(string? permissionMatrixJson)
        => PermissionMatrixBuilder.Parse(permissionMatrixJson).Count > 0;

    private static bool IsPermissionGroup(CoverageMapItem item)
        => (item.Label ?? string.Empty).Trim().StartsWith("Phân quyền", StringComparison.OrdinalIgnoreCase);
}
