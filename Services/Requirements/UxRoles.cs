using ICOGenerator.Contracts.Requirements;
using static ICOGenerator.Services.Requirements.RequirementText;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Danh sách VAI của ứng dụng cho bản thiết kế UX: người làm các bước luồng đã chốt, người làm các tác vụ,
/// và các cột vai của bảng phân quyền (người dùng sửa được ở popup — vai họ thêm tay cũng là vai thật).
///
/// <para>
/// "Hệ thống" không phải một vai: bước do ứng dụng tự làm (tự gửi email, tự đổi trạng thái) không có ai mở
/// menu để làm nó, nên dựng điều hướng cho "Hệ thống" là dựng một bộ menu không có người dùng. Cùng lý do,
/// người chỉ làm bước NGOÀI PHẦN MỀM (ca thật: HR gửi file nghỉ việc qua email) cũng không phải một vai.
/// </para>
/// </summary>
public static class UxRoles
{
    private static readonly string[] SystemActors =
        { "hệ thống", "system", "ứng dụng", "app", "application", "tự động", "automatic", "auto", "scheduler", "job" };

    public static bool IsSystem(string? actor)
    {
        var key = Normalize(actor);
        return key.Length == 0 || SystemActors.Contains(key);
    }

    public static List<string> Collect(string? flowMapJson, IEnumerable<UxTask>? tasks, string? permissionMatrixJson)
    {
        var result = new List<string>();
        void Add(string? raw)
        {
            var value = OneLine(raw);
            if (IsSystem(value) || result.Any(r => Normalize(r) == Normalize(value)))
                return;
            if (result.Count < PermissionMatrixBuilder.MaxRoles)
                result.Add(value);
        }

        foreach (var flow in FlowMapBuilder.Parse(flowMapJson))
        {
            Add(flow.Role);
            // Người làm bước NGOÀI PHẦN MỀM không phải người dùng của ứng dụng (HR gửi file qua email): dựng menu
            // cho họ là dựng một bộ menu rỗng không ai mở.
            foreach (var step in flow.Steps.Where(s => s.Included && !s.OutsideSoftware))
                Add(step.Actor);
        }
        foreach (var actor in (tasks ?? Enumerable.Empty<UxTask>()).SelectMany(t => t.Actors))
            Add(actor);
        foreach (var role in PermissionMatrixBuilder.Roles(permissionMatrixJson))
            Add(role);

        return result;
    }
}
