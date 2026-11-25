using AngleSharp.Dom;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Luật ngôn ngữ của POC: <b>NHÃN GIAO DIỆN luôn tiếng Anh, chỉ NỘI DUNG nghiệp vụ được tiếng Việt</b>.
/// <para>
/// Nhãn giao diện là mọi chữ mà khung ứng dụng đặt ra để người dùng thao tác: nút, tiêu đề cột, nhãn ô
/// nhập, placeholder, tooltip, tab, chip trạng thái, dòng gợi ý của dropdown, nhãn menu. Nội dung là chữ
/// của BẢN GHI: dữ liệu mẫu, mô tả công việc, văn bản người dùng nhập. Luật này cùng một gốc với luật giao
/// diện của chính ICOGenerator (xem <c>docs/architecture.md</c>): một bản demo trộn "Tạo JD" với
/// "Submit" và "Version History" đọc như ba ứng dụng ghép lại, còn một khung tiếng Anh chở dữ liệu tiếng
/// Việt là hình dạng của ứng dụng thật mà đơn vị sẽ nhận.
/// </para>
/// <para>
/// Phép kiểm TẤT ĐỊNH và cố ý chỉ nhìn chỗ chắc chắn là nhãn: một dấu tiếng Việt trong đó là bằng chứng
/// đủ, nên báo ISSUE. Nó KHÔNG đọc chữ mà script dựng lúc chạy (dòng "không có dữ liệu", toast) — chuỗi
/// trong script lẫn với dữ liệu seed tiếng Việt hợp lệ, phân biệt bằng máy là đoán; phần đó do prompt
/// và Visual QA giữ. <c>&lt;option&gt;</c> có giá trị cũng không kiểm: đó thường là bản ghi danh mục
/// (tên phòng ban, chức danh), tức nội dung.
/// </para>
/// </summary>
public static class PocUiLanguageCheck
{
    private const string VietnameseDiacritics =
        "àáảãạăằắẳẵặâầấẩẫậđèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵ";

    private const int MaxExamples = 8;

    // Phần tử mà TOÀN BỘ chữ bên trong là nhãn giao diện. Cố ý không có h1–h6/card-header: tiêu đề khung
    // chi tiết thường là tên của chính bản ghi đang mở.
    private static readonly (string Selector, string Kind)[] ChromeElements =
    [
        ("button, .btn", "button"),
        ("th", "column"),
        ("label, .form-label", "label"),
        ("legend", "legend"),
        (".modal-title", "modal title"),
        (".nav-tabs .nav-link, .nav-pills .nav-link", "tab"),
        (".badge", "status chip"),
        (".dropdown-item", "dropdown item"),
        ("option[value='']", "dropdown prompt"),
    ];

    private static readonly string[] ChromeAttributes = ["placeholder", "title", "aria-label"];

    /// <summary>
    /// Trả về danh sách ISSUE (rỗng hoặc đúng một mục gom mọi chỗ sai) cho vùng POC_CONTENT của
    /// <paramref name="html"/> và các nhãn NHÓM menu do agent đặt (<paramref name="navGroupLabels"/>).
    /// Nhãn mục lá không kiểm ở đây: nó là tên màn hình của spec, phải chép nguyên văn để khớp data-view.
    /// </summary>
    public static IReadOnlyList<string> Inspect(string html, IEnumerable<string>? navGroupLabels = null)
    {
        var offenders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string text, string kind)
        {
            text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length == 0 || !HasVietnamese(text) || !seen.Add(text))
                return;
            offenders.Add($"'{Shorten(text)}' ({kind})");
        }

        foreach (var group in navGroupLabels ?? [])
            Add(group, "menu group");

        var content = string.IsNullOrWhiteSpace(html) ? string.Empty : PocTemplate.GetContentBody(html);
        if (content.Length > 0)
        {
            var dom = PocDom.Parse(content);
            foreach (var (selector, kind) in ChromeElements)
            {
                foreach (var el in dom.QuerySelectorAll(selector))
                {
                    // <label> bọc checkbox/radio mang tên một giá trị dữ liệu, không phải nhãn ô nhập.
                    if (kind == "label" && el.QuerySelector("input[type=checkbox], input[type=radio]") != null)
                        continue;
                    Add(PocDom.VisibleText(el.InnerHtml), kind);
                }
            }

            foreach (var attr in ChromeAttributes)
            {
                foreach (var el in dom.QuerySelectorAll($"[{attr}]"))
                    Add(el.GetAttribute(attr) ?? string.Empty, attr);
            }

            foreach (var el in dom.QuerySelectorAll("input[type=button][value], input[type=submit][value]"))
                Add(el.GetAttribute("value") ?? string.Empty, "button");
        }

        if (offenders.Count == 0)
            return [];

        var examples = string.Join(", ", offenders.Take(MaxExamples))
                       + (offenders.Count > MaxExamples ? $" … (+{offenders.Count - MaxExamples})" : string.Empty);
        return
        [
            $"Nhãn giao diện của POC còn tiếng Việt ({offenders.Count} chỗ: {examples}). "
            + "Luật ngôn ngữ của POC: MỌI nhãn giao diện — nút, tiêu đề cột, nhãn ô nhập, placeholder, tooltip, tab, chip trạng thái, "
            + "dòng gợi ý của dropdown, nhãn menu, tên vai, thông báo/toast — viết bằng TIẾNG ANH; chỉ NỘI DUNG nghiệp vụ (dữ liệu mẫu, mô tả, "
            + "văn bản của bản ghi) được để tiếng Việt. Dịch các nhãn trên sang tiếng Anh (vd 'Create JD', 'Search by Employee ID or Full Name') "
            + "rồi gọi lại SetPocContent — nhãn hành động của bản thiết kế UX và tên màn hình thì chép NGUYÊN VĂN từ spec."
        ];
    }

    /// <summary>Chuỗi có ít nhất một chữ cái mang dấu tiếng Việt.</summary>
    public static bool HasVietnamese(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var c in text)
        {
            if (VietnameseDiacritics.Contains(char.ToLowerInvariant(c)))
                return true;
        }
        return false;
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "…";
}
