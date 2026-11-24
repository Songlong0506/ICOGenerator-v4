using System.Text.Json.Serialization;

namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Bộ kịch bản UAT sinh từ AI Design Spec TRƯỚC khi POC được dựng — vừa là đích máy móc của POC, vừa hiển
/// thị thành hướng dẫn sử dụng từng bước ở trang POC Review.
/// Lưu tại <c>04_Implementation/uat-scenarios.json</c> trong workspace của project.
/// </summary>
public class UatScenarioSet
{
    public List<UatScenario> Scenarios { get; set; } = new();
}

public class UatScenario
{
    public string Title { get; set; } = string.Empty;

    /// <summary>Màn hình chính của kịch bản (nguyên văn tên trong spec) — dùng gắn ghi chú khi báo lỗi.</summary>
    public string Screen { get; set; } = string.Empty;

    /// <summary>Mã Business Rule (BR-n) kịch bản này kiểm chứng; rỗng nếu không gắn rule nào.</summary>
    public List<string> RuleRefs { get; set; } = new();

    /// <summary>Câu chữ của từng bước — thứ NGƯỜI đọc (hướng dẫn sử dụng ở POC Review, prompt dựng POC).</summary>
    public List<string> Steps { get; set; } = new();

    /// <summary>
    /// Thứ MÁY đọc: <c>StepActions[j]</c> là các thao tác của bước <c>Steps[j]</c>, theo đúng thứ tự làm. Do
    /// chính BA viết bước sinh ra cùng lúc với câu chữ, nên lượt lái thật (<c>PocRuntimeChecker</c>) không
    /// phải đoán ý định từ một câu tiếng Việt — câu "Mở 'Danh sách' và bấm dòng ĐƠN-09" là HAI thao tác,
    /// câu "Nhập Tên là 'ABC'" là gõ giá trị vào ô Tên chứ không phải tìm nút tên "ABC".
    /// Rỗng (bộ kịch bản sinh trước khi có trường này) ⇒ lượt lái lùi về đoán theo câu chữ.
    /// </summary>
    public List<List<UatAction>> StepActions { get; set; } = new();

    /// <summary>Có kế hoạch máy-đọc-được cho MỌI bước không (bộ kịch bản kiểu mới).</summary>
    [JsonIgnore]
    public bool HasActionPlan => Steps.Count > 0 && StepActions.Count == Steps.Count;

    /// <summary>Kế hoạch thao tác của bước <paramref name="index"/>; rỗng nếu bộ kịch bản không có kế hoạch.</summary>
    public IReadOnlyList<UatAction> ActionsOf(int index) =>
        HasActionPlan && index >= 0 && index < StepActions.Count ? StepActions[index] : Array.Empty<UatAction>();
}

/// <summary>Một thao tác của một bước kịch bản — xem <see cref="UatActionKind"/> cho nghĩa của từng loại.</summary>
public class UatAction
{
    /// <summary>Một giá trị của <see cref="UatActionKind"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Thứ thao tác nhắm tới, NGUYÊN VĂN như trên giao diện: tên vai, tên màn hình, nhãn nút/tab/liên kết hoặc
    /// chữ định danh một dòng (mã bản ghi), nhãn ô nhập, hoặc chữ cần thấy (bước kiểm tra).
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Giá trị gõ vào (<see cref="UatActionKind.Fill"/>) hoặc lựa chọn (<see cref="UatActionKind.Select"/>).</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Các loại thao tác. Cố ý ít và không gắn với nghiệp vụ nào — đủ mô tả mọi kịch bản dùng app trên một bản
/// demo HTML: đổi vai, mở màn, bấm, nhập, chọn, nhìn.
/// </summary>
public static class UatActionKind
{
    /// <summary>Chọn vai ở khối VIEW AS. Target = tên vai.</summary>
    public const string Role = "role";

    /// <summary>Mở một màn hình từ menu trái. Target = tên màn hình (nguyên văn trong spec).</summary>
    public const string Open = "open";

    /// <summary>Bấm một nút / tab / liên kết / mục, hoặc một dòng (Target = chữ định danh dòng, vd mã bản ghi).</summary>
    public const string Click = "click";

    /// <summary>Gõ <see cref="UatAction.Value"/> vào ô nhập có nhãn Target.</summary>
    public const string Fill = "fill";

    /// <summary>Chọn <see cref="UatAction.Value"/> trong ô chọn / nhóm lựa chọn có nhãn Target.</summary>
    public const string Select = "select";

    /// <summary>Quan sát: Target là chữ phải nhìn thấy trên màn. Không bấm gì.</summary>
    public const string Check = "check";

    public static readonly IReadOnlyList<string> All = [Role, Open, Click, Fill, Select, Check];

    public static bool IsKnown(string? kind) => kind != null && All.Contains(kind);

    /// <summary>Loại cần <see cref="UatAction.Value"/> mới có nghĩa.</summary>
    public static bool NeedsValue(string kind) => kind == Fill;

    /// <summary>
    /// Câu mô tả MỘT thao tác — dùng chung cho khối prompt giao đích (agent biết máy sẽ bấm/gõ đúng chữ nào)
    /// và cho báo cáo lái thật (agent sửa biết máy đã làm gì trước chỗ gãy). Một chỗ sinh chữ để hai phía
    /// luôn gọi cùng một thao tác bằng cùng một câu.
    /// </summary>
    public static string Describe(UatAction action) => action.Kind switch
    {
        Role => $"chọn vai «{action.Target}» ở VIEW AS",
        Open => $"mở màn «{action.Target}» ở menu",
        Click => $"bấm «{action.Target}»",
        Fill => $"gõ «{action.Value}» vào ô «{action.Target}»",
        Select => string.IsNullOrWhiteSpace(action.Value)
            ? $"chọn «{action.Target}»"
            : $"chọn «{action.Value}» ở «{action.Target}»",
        Check => $"nhìn thấy «{action.Target}»",
        _ => $"{action.Kind} «{action.Target}»"
    };
}
