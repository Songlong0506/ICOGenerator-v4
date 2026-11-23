using ICOGenerator.Localization;

namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// KHOÁ của 11 nhóm thông tin trong bản đồ bao phủ — định danh không phụ thuộc ngôn ngữ. Model chép khoá
/// vào trường <c>key</c> của từng dòng (khung nằm ở <c>requirement-coverage.v5.md</c>); mọi cổng và guard
/// nhận ra một nhóm bằng khoá này, KHÔNG bằng nhãn.
///
/// <para>
/// Trước đây nhãn tiếng Việt làm luôn việc định danh: mỗi cổng giữ một hằng nhãn (hoặc một tiền tố của nó)
/// và so <c>StartsWith</c>. Nhãn là thứ người dùng đọc trên panel tiến độ, nên nó phải đổi theo ngôn ngữ —
/// và ngày nó đổi thì mọi cổng câm cùng lúc mà không test nào báo.
/// </para>
/// </summary>
public static class CoverageGroupKey
{
    public const string Goal = "goal";
    public const string Roles = "roles";
    public const string MainFlow = "main-flow";
    public const string CurrentProcess = "current-process";
    public const string Exceptions = "exceptions";
    public const string Data = "data";
    public const string Rules = "rules";
    public const string Lifecycle = "lifecycle";
    public const string Notifications = "notifications";
    public const string Reports = "reports";
    public const string Scale = "scale";

    /// <summary>
    /// Đủ 11 khoá, theo đúng thứ tự dòng của bản đồ. KHÔNG có nhóm phân quyền: bảng phân quyền do BA tự soạn ở
    /// bước "Write Requirement" (xem <c>PermissionMatrixGate</c>), không phải thứ được khai thác bằng câu hỏi.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Goal, Roles, MainFlow, CurrentProcess, Exceptions, Data,
        Rules, Lifecycle, Notifications, Reports, Scale
    };
}

/// <summary>
/// Cầu nối giữa KHOÁ nhóm và NHÃN nhóm. Nhãn nằm ở resource (<c>CoverageGroup.Label.&lt;khoá&gt;</c>), mỗi
/// ngôn ngữ một bản; <c>CoverageGroupsTests</c> chốt rằng bản tiếng Việt trùng từng chữ với khung trong
/// prompt coverage.
/// </summary>
public static class CoverageGroups
{
    /// <summary>
    /// Khoá của nhóm mà <paramref name="labelOrKey"/> chỉ tới, hoặc <c>null</c> khi không nhóm nào khớp
    /// (model tự nghĩ ra một tên nhóm).
    ///
    /// <para>
    /// Dùng cho những chuỗi nhóm do MODEL viết mà không kèm khoá: trường <c>group</c> của một câu hỏi, và
    /// dòng bản đồ model lỡ bỏ <c>key</c>. So với nhãn ở MỌI ngôn ngữ được hỗ trợ — hội thoại bằng ngôn
    /// ngữ nào thì model chép nhãn của ngôn ngữ đó. Khớp hai chiều bằng TIỀN TỐ: model viết
    /// "Luồng ngoại lệ" thay cho "Luồng ngoại lệ &amp; trường hợp đặc biệt" thì vẫn là một nhóm. Một mẩu
    /// ngắn tới mức là tiền tố của HAI nhãn ("Quy") thì không chỉ tới nhóm nào ⇒ <c>null</c>: gán bừa vào
    /// nhóm đầu tiên là để một cổng mở nhầm.
    /// </para>
    /// </summary>
    public static string? KeyOf(string? labelOrKey)
    {
        var value = (labelOrKey ?? string.Empty).Trim();
        if (value.Length == 0)
            return null;

        var exact = CoverageGroupKey.All.FirstOrDefault(k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        string? found = null;
        foreach (var key in CoverageGroupKey.All)
        {
            foreach (var label in Loc.InAllLanguages(LabelResource(key)))
            {
                if (string.Equals(label, value, StringComparison.OrdinalIgnoreCase))
                    return key;

                if (!label.StartsWith(value, StringComparison.OrdinalIgnoreCase)
                    && !value.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (found != null && found != key)
                    return null;
                found = key;
            }
        }

        return found;
    }

    private static string LabelResource(string key) => "CoverageGroup.Label." + key;
}
