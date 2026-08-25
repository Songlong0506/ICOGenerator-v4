using System.Globalization;
using System.Text;

namespace ICOGenerator.Services.Artifacts;

/// <summary>Nhóm menu mà một màn hình của POC thuộc về — xem <see cref="PocNavGroups"/>.</summary>
public enum PocNavGroupKind
{
    /// <summary>Màn hình nghiệp vụ bình thường: đứng thẳng ở menu gốc.</summary>
    None = 0,

    /// <summary>Màn hình quản lý một DANH MỤC do ứng dụng tự quản lý ("&lt;tên&gt; Catalog").</summary>
    Catalog,

    /// <summary>Màn hình BÁO CÁO / thống kê ("&lt;tên&gt; Report").</summary>
    Report
}

/// <summary>
/// Phân loại tên màn hình thành nhóm menu của bản demo: danh mục, báo cáo, hay màn hình nghiệp vụ
/// thường. Đây là chỗ DUY NHẤT biết "màn hình này đáng lẽ nằm trong nhóm nào" — prompt dựng POC nói
/// luật bằng lời, <see cref="PocAudit"/> soát bằng chính hàm này.
///
/// <para>
/// <b>Vì sao phải gom.</b> Hai bảng của buổi phỏng vấn đẻ ra màn hình theo LÔ chứ không lẻ tẻ: mỗi
/// thông tin kiểu CHỌN mà nguồn là "ứng dụng tự quản lý" thành một màn hình
/// <c>"&lt;tên&gt; Catalog"</c> (<c>EntityMapBuilder.ManagedListScreens</c>), mỗi dòng bảng báo cáo
/// thành một màn hình <c>"&lt;tên&gt; Report"</c> (<c>ReportMapBuilder.ReportScreens</c>). Một dự án
/// nhân sự bình thường có 5–8 danh mục và 3–5 báo cáo, nên để phẳng thì sidebar của bản demo dài gấp
/// đôi phần nghiệp vụ thật và người xem demo phải cuộn qua một dãy màn CRUD giống hệt nhau mới tới
/// được luồng chính — đúng thứ họ mở demo để xem. Gom vào MỘT mục xổ xuống là cách mọi ứng dụng quản
/// trị thật làm, và shell của POC đã hỗ trợ sẵn nhóm (<c>PocNavItem.Children</c>).
/// </para>
///
/// <para>
/// <b>Phân loại theo TÊN, và cố ý hẹp.</b> Tên màn hình là thứ duy nhất chảy tới được bước dựng POC
/// (spec chỉ có <c>## 6. Screens To Generate</c>), nhưng nó cũng đủ tin: hai hàm gieo tên ở trên đã
/// gắn sẵn hậu tố <c>Catalog</c>/<c>Report</c>, và người dùng sửa tên ở bảng màn hình thì thường giữ
/// hậu tố ấy. Danh sách từ khoá vì thế chỉ nhận cái CHẮC CHẮN: <c>dashboard</c> và <c>overview</c>
/// KHÔNG có trong danh sách báo cáo dù <c>ReportMapBuilder</c> coi chúng là "tên tự đọc được như một
/// màn hình" — một "Employee Dashboard" thường là màn CHỦ của một vai, và một cổng bắt gom nhầm màn
/// chủ vào nhóm Báo cáo là một cổng agent sẽ học cách phớt lờ. Bỏ sót thì im lặng, bắt nhầm thì ồn.
/// </para>
/// </summary>
public static class PocNavGroups
{
    /// <summary>
    /// Từ bao nhiêu màn hình cùng loại trở lên thì BẮT BUỘC gom. Hai màn hình để phẳng vẫn đọc được,
    /// mà gom lại còn bắt người xem bấm thêm một lượt để thấy chúng; ba là lúc dãy màn giống nhau bắt
    /// đầu đè lên phần nghiệp vụ của sidebar.
    /// </summary>
    public const int MinGroupSize = 3;

    // Chỉ tra trên tên đã bỏ dấu + thường hoá, nên "Danh Mục Chức Danh" và "danh muc chuc danh" là một.
    private static readonly string[] CatalogWords = ["catalog", "catalogue", "danh muc"];

    private static readonly string[] ReportWords = ["report", "bao cao", "thong ke", "statistic", "analytic"];

    // Tên TRẦN chỉ gồm đúng từ khoá: đó là tiêu đề NHÓM ("Reports", "Danh mục") hoặc màn chủ
    // ("Dashboard", "Overview"), không phải một thành viên của nhóm. Xếp chúng vào nhóm là tự đếm
    // tiêu đề nhóm thành thành viên của chính nó.
    private static readonly string[] BareNames =
    [
        "catalog", "catalogs", "catalogue", "catalogues", "danh muc", "master data",
        "report", "reports", "bao cao", "thong ke", "statistic", "statistics", "analytic", "analytics",
        "dashboard", "overview", "home", "trang chu"
    ];

    /// <summary>
    /// Nhóm của một tên màn hình / nhãn mục menu. Danh mục được xét TRƯỚC báo cáo: "Report Type
    /// Catalog" là màn quản lý danh mục loại báo cáo, không phải một báo cáo.
    /// </summary>
    public static PocNavGroupKind Classify(string? screenName)
    {
        var name = Fold(screenName);
        if (name.Length == 0 || BareNames.Contains(name, StringComparer.Ordinal))
            return PocNavGroupKind.None;

        if (CatalogWords.Any(w => name.Contains(w, StringComparison.Ordinal)))
            return PocNavGroupKind.Catalog;

        return ReportWords.Any(w => name.Contains(w, StringComparison.Ordinal))
            ? PocNavGroupKind.Report
            : PocNavGroupKind.None;
    }

    /// <summary>Tên loại để đưa vào câu báo lỗi ("3 màn hình danh mục…").</summary>
    public static string Describe(PocNavGroupKind kind) => kind switch
    {
        PocNavGroupKind.Catalog => "danh mục",
        PocNavGroupKind.Report => "báo cáo",
        _ => "khác"
    };

    /// <summary>Ví dụ nhãn nhóm gợi ý cho agent — nó tự chọn ngôn ngữ theo spec, đây chỉ là gợi ý.</summary>
    public static string SampleLabel(PocNavGroupKind kind) => kind switch
    {
        PocNavGroupKind.Catalog => "\"Danh mục\" / \"Catalogs\"",
        PocNavGroupKind.Report => "\"Báo cáo\" / \"Reports\"",
        _ => "\"Khác\""
    };

    /// <summary>Các loại có đủ <see cref="MinGroupSize"/> màn hình trong <paramref name="screenNames"/>.</summary>
    public static List<PocNavGroupKind> KindsToGroup(IEnumerable<string>? screenNames)
        => (screenNames ?? Enumerable.Empty<string>())
            .Select(Classify)
            .Where(k => k != PocNavGroupKind.None)
            .GroupBy(k => k)
            .Where(g => g.Count() >= MinGroupSize)
            .Select(g => g.Key)
            .OrderBy(k => k)
            .ToList();

    // Thường hoá + bỏ dấu + gộp khoảng trắng: "Báo cáo Nhân sự" ⇒ "bao cao nhan su". Dấu câu thành
    // khoảng trắng để "PC-Level Catalog" không dính thành một từ.
    private static string Fold(string? text)
    {
        var raw = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Length == 0)
            return string.Empty;

        var sb = new StringBuilder(raw.Length);
        var lastWasSpace = true;
        foreach (var ch in raw.Normalize(NormalizationForm.FormD))
        {
            // đ không phải "d + dấu" — FormD không tách nó ra nên phải xử tay (cùng lý do với
            // Services/Requirements/ExportFileName.cs).
            var c = ch == 'đ' ? 'd' : ch;
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }
}
