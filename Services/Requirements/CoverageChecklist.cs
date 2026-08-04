using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Prompts;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// KHUNG RỖNG của "Bản đồ bao phủ yêu cầu": đủ 12 nhóm thông tin, tất cả ở <c>[CHƯA HỎI]</c> — dùng cho
/// dự án chưa có bản đồ nào (vừa tạo, hoặc vừa "New Chat" nên <see cref="Domain.Project.RequirementCoverageMap"/>
/// bị xoá về null). Panel "Tiến độ khai thác" trước đây ẩn ở đúng lúc này, mà đó lại là lúc nó có giá trị
/// nhất: người dùng cần thấy TRƯỚC cuộc phỏng vấn gồm những nhóm gì và có điểm dừng — thay vì cảm giác
/// "bị hỏi vô tận" mà panel sinh ra để chữa. Ẩn panel còn khiến tooltip của nút "Write Requirement"
/// ("hoàn tất các nhóm còn lại trong 'Tiến độ khai thác'") trỏ tới một panel không có trên màn hình.
/// <para>
/// Danh sách nhóm KHÔNG hard-code ở đây: nó được bóc thẳng từ khối template trong
/// <c>Prompts/BusinessAnalyst/requirement-coverage.v3.md</c> — cùng file quy định 12 dòng mà LLM phải
/// xuất. Một nguồn chân lý duy nhất, nên khung rỗng lúc mới vào và bản đồ thật sau lượt chat đầu tiên
/// KHÔNG THỂ lệch nhau về tên nhóm hay thứ tự (nếu hard-code, sửa prompt là danh sách "nhảy chữ" ngay
/// trước mắt người dùng). Fail-open: bóc/parse không ra dòng nào ⇒ danh sách rỗng ⇒ panel ẩn đúng như
/// hành vi cũ, không chặn trang.
/// </para>
/// </summary>
public class CoverageChecklist
{
    public const string CoveragePromptPath = "BusinessAnalyst/requirement-coverage.v3.md";

    // Placeholder trạng thái trong khối template của prompt — cũng là dấu hiệu nhận ra ĐÚNG khối đó,
    // phân biệt với khối ví dụ (chứa trạng thái thật, vd "[RÕ]") ở phần dưới cùng file prompt.
    private const string StatusPlaceholder = "[TRẠNG THÁI]";

    private readonly PromptTemplateService _prompts;

    // Prompt không đổi trong vòng đời một request (service scoped) nên chỉ bóc một lần cho mỗi lần dựng.
    private IReadOnlyList<CoverageMapItem>? _cached;

    public CoverageChecklist(PromptTemplateService prompts)
    {
        _prompts = prompts;
    }

    /// <summary>12 nhóm ở trạng thái [CHƯA HỎI] để render panel khi dự án chưa có bản đồ nào.</summary>
    public IReadOnlyList<CoverageMapItem> Skeleton() => _cached ??= Parse(_prompts.Get(CoveragePromptPath));

    /// <summary>
    /// Bóc danh sách nhóm từ nội dung prompt coverage. Tách riêng (static, nhận text) để test chốt được
    /// khung này khớp file prompt THẬT — sửa tên/thứ tự nhóm trong prompt mà quên gì đó sẽ fail ở test
    /// chứ không âm thầm đổi màn hình.
    /// </summary>
    public static IReadOnlyList<CoverageMapItem> Parse(string? promptText)
    {
        var template = ExtractTemplateBlock(promptText);
        if (template == null)
            return Array.Empty<CoverageMapItem>();

        // Dùng lại đúng parser của bản đồ thật: khối template có cùng dạng dòng, và "[TRẠNG THÁI]" không
        // phải trạng thái hợp lệ nên CoverageMapParser tự chuẩn hoá về [CHƯA HỎI]. Phần tóm tắt chỉ là
        // placeholder "<tóm tắt …>" của prompt nên xoá đi — dòng CHƯA HỎI vốn không có tóm tắt.
        var items = CoverageMapParser.Parse(template);
        foreach (var item in items)
        {
            item.Status = "CHƯA HỎI";
            item.Summary = string.Empty;
            item.Evidence = string.Empty;
        }
        return items;
    }

    // Khối ``` đầu tiên có chứa placeholder trạng thái. Đi theo hàng rào ``` thay vì regex trên cả file
    // để không vơ nhầm khối VÍ DỤ (cũng là dòng bản đồ hợp lệ, nhưng chỉ có một dòng).
    private static string? ExtractTemplateBlock(string? promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText))
            return null;

        var lines = promptText.Replace("\r\n", "\n").Split('\n');
        List<string>? block = null;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (block == null)
                {
                    block = new List<string>();
                    continue;
                }

                // Hết một khối: giữ nếu đúng là khối template, không thì đọc tiếp khối sau.
                var content = string.Join("\n", block);
                if (content.Contains(StatusPlaceholder, StringComparison.Ordinal))
                    return content;

                block = null;
                continue;
            }

            block?.Add(line);
        }

        return null;
    }
}
