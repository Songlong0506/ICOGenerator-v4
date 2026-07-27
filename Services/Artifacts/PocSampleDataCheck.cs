using System.Net;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Bối cảnh "dữ liệu thật" của một lượt dựng POC: bản markdown của AI Design Spec (để biết POC PHẢI
/// nói ngôn ngữ nào) và text bóc từ các file Excel/Word người dùng đã đính kèm (danh mục, biểu mẫu thật
/// của đơn vị yêu cầu). Cả hai đều tùy chọn — thiếu phần nào thì phép kiểm tương ứng tự bỏ qua.
/// </summary>
public sealed record PocSampleDataContext(string? SpecMarkdown, string? RealSampleText)
{
    public static readonly PocSampleDataContext Empty = new(null, null);
}

/// <summary>
/// Kiểm DỮ LIỆU MẪU và NGÔN NGỮ của POC — tầng mà mọi cổng khác đều mù.
/// <para>
/// Prompt sinh spec đã nạp text bóc từ Excel/Word của người dùng làm "dữ liệu mẫu THẬT"
/// (<c>RequirementDocsService.BuildRealSampleDataAsync</c>) và prompt POC yêu cầu UI dùng đúng ngôn ngữ
/// của spec — nhưng KHÔNG có gì kiểm chứng cả hai. Hệ quả là lớp lỗi đắt nhất về mặt niềm tin: người
/// dùng nghiệp vụ mở demo và thấy "Sản phẩm A / Nguyễn Văn B" thay vì đúng danh mục họ vừa gửi, hoặc
/// thấy một UI tiếng Anh cho một spec tiếng Việt. Audit tĩnh bảo "OK" (wiring đủ), self-test PASS
/// (công thức đúng), Visual QA thì chỉ chạy khi có agent vision — nên bản demo vẫn đi qua mọi cổng.
/// </para>
/// <para>
/// Phép kiểm ở đây là scan chuỗi TẤT ĐỊNH trên vùng POC_CONTENT (không tính shell — shell có sẵn chữ
/// mẫu riêng), và cố tình dè dặt: chỉ báo ISSUE khi bằng chứng rõ ràng (không một token nào của tài
/// liệu thật xuất hiện; UI không có lấy một dấu tiếng Việt trong khi spec đầy), còn lại là WARNING.
/// </para>
/// </summary>
public static partial class PocSampleDataCheck
{
    // Ứng viên token lấy từ tài liệu thật: đủ dài để mang tính đặc trưng (từ ngắn như "Ngày", "Tên"
    // xuất hiện trong mọi POC tiếng Việt nên không chứng minh được gì).
    private const int MinTokenLength = 6;
    private const int MaxTokenLength = 60;
    private const int MaxTokens = 200;
    // Dưới ngưỡng này thì tài liệu quá mỏng để kết luận "POC phớt lờ dữ liệu thật".
    private const int MinTokensToJudge = 8;
    // Số token khớp coi là "đã thật sự dùng dữ liệu của đơn vị"; ít hơn ⇒ WARNING (0 ⇒ ISSUE).
    private const int GoodHitCount = 3;

    // Spec phải "đậm đặc" tiếng Việt tới mức này mới kết luận được ngôn ngữ của nó.
    private const int SpecDiacriticsForVietnamese = 20;
    // Chữ hiển thị của POC có ít hơn ngần này dấu tiếng Việt = UI không phải tiếng Việt.
    private const int PocDiacriticsFloor = 5;

    private const string VietnameseDiacritics =
        "àáảãạăằắẳẵặâầấẩẫậđèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵ";

    // Tên/nhãn "bịa cho có" mà người dùng nghiệp vụ nhận ra ngay. Cố ý hẹp: chỉ các mẫu placeholder
    // kinh điển, để không bắt nhầm dữ liệu thật (một sản phẩm TÊN LÀ "Sản phẩm mẫu" là chuyện khác).
    private static readonly (Regex Pattern, string Label)[] Placeholders =
    [
        (PlaceholderPersonViRegex(), "tên người bịa (Nguyễn Văn A / Trần Thị B…)"),
        (PlaceholderEntityViRegex(), "danh mục bịa (Sản phẩm A, Khách hàng B, Đơn hàng 1…)"),
        (PlaceholderCompanyViRegex(), "tên đơn vị bịa (Công ty ABC / XYZ)"),
        (PlaceholderPersonEnRegex(), "tên người bịa tiếng Anh (John Doe / Jane Doe)"),
        (PlaceholderEntityEnRegex(), "danh mục bịa tiếng Anh (Item 1, Product A, Customer B…)"),
        (LoremRegex(), "văn bản độn Lorem ipsum"),
        (PlaceholderEmailRegex(), "email bịa (example.com / test@test…)"),
    ];

    /// <summary>Kết quả một lượt kiểm: nối thẳng vào danh sách ISSUES/WARNINGS của <see cref="PocAudit"/>.</summary>
    public sealed record Result(IReadOnlyList<string> Issues, IReadOnlyList<string> Warnings)
    {
        public static readonly Result Empty = new(Array.Empty<string>(), Array.Empty<string>());
    }

    /// <param name="html">Toàn bộ poc-demo.html (vùng content được cắt ra bên trong).</param>
    public static Result Inspect(string html, PocSampleDataContext? context)
    {
        if (context == null || string.IsNullOrWhiteSpace(html))
            return Result.Empty;

        var content = PocTemplate.GetContentBody(html);
        if (content.Length == 0)
            return Result.Empty;

        var visible = VisibleText(content);
        if (visible.Length == 0)
            return Result.Empty;

        var issues = new List<string>();
        var warnings = new List<string>();

        CheckRealSampleData(visible, context.RealSampleText, issues, warnings);
        CheckPlaceholders(visible, hasRealData: HasUsableSample(context.RealSampleText), issues, warnings);
        CheckLanguage(visible, context.SpecMarkdown, issues);

        return new Result(issues, warnings);
    }

    // POC có dùng đúng danh mục/tên trong file người dùng gửi không. Không có file ⇒ không kiểm (POC
    // dựng từ dữ liệu tự nghĩ là hợp lệ khi người dùng chẳng đưa gì).
    private static void CheckRealSampleData(string visible, string? realSampleText, List<string> issues, List<string> warnings)
    {
        var tokens = ExtractTokens(realSampleText);
        if (tokens.Count < MinTokensToJudge)
            return;

        var hits = tokens.Where(t => visible.Contains(t, StringComparison.OrdinalIgnoreCase)).Take(GoodHitCount).ToList();
        if (hits.Count >= GoodHitCount)
            return;

        var samples = string.Join(", ", tokens.Take(6).Select(t => $"\"{t}\""));
        if (hits.Count == 0)
        {
            issues.Add(
                "Dữ liệu mẫu của POC KHÔNG dùng gì từ tài liệu người dùng đã đính kèm (Excel/Word): không một tên/danh mục nào trong tài liệu xuất hiện trên màn hình. "
                + $"Seed lại dữ liệu demo bằng chính các giá trị THẬT trong tài liệu (ví dụ: {samples}) — người dùng nghiệp vụ mở demo và thấy dữ liệu bịa sẽ mất tin tưởng ngay, dù mọi tính toán đều đúng.");
            return;
        }

        warnings.Add(
            $"POC chỉ dùng {hits.Count} giá trị lấy từ tài liệu người dùng đính kèm — phần lớn dữ liệu demo vẫn là tự nghĩ. "
            + $"Ưu tiên seed bằng danh mục/tên thật trong tài liệu (ví dụ: {samples}) để demo nói đúng ngôn ngữ nghiệp vụ của đơn vị yêu cầu.");
    }

    // Placeholder kinh điển: có tài liệu thật mà vẫn bịa ⇒ ISSUE (agent bỏ qua thứ đã được đưa tận tay);
    // không có tài liệu ⇒ WARNING (vẫn nên đặt tên nghe như thật, nhưng không có nguồn nào để đối chiếu).
    private static void CheckPlaceholders(string visible, bool hasRealData, List<string> issues, List<string> warnings)
    {
        var found = new List<string>();
        foreach (var (pattern, label) in Placeholders)
        {
            var match = pattern.Match(visible);
            if (match.Success)
                found.Add($"{label} — vd \"{match.Value.Trim()}\"");
        }

        if (found.Count == 0)
            return;

        var detail = string.Join("; ", found.Take(4));
        if (hasRealData)
            issues.Add(
                $"Dữ liệu mẫu trên POC còn dùng tên bịa trong khi người dùng ĐÃ đính kèm tài liệu có dữ liệu thật: {detail}. "
                + "Thay bằng giá trị thật lấy từ tài liệu đó.");
        else
            warnings.Add(
                $"Dữ liệu mẫu trên POC dùng tên placeholder: {detail}. "
                + "Đặt tên/danh mục nghe như dữ liệu thật của nghiệp vụ trong spec — người xem demo đánh giá độ tin cậy qua chính mấy dòng dữ liệu này.");
    }

    // Ngôn ngữ UI phải khớp ngôn ngữ spec. Chỉ kiểm được chiều "spec tiếng Việt ⇒ UI phải tiếng Việt":
    // đó là chiều sai thật sự hay xảy ra (model rơi về tiếng Anh) và là chiều đo được tất định bằng dấu.
    private static void CheckLanguage(string visible, string? specMarkdown, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(specMarkdown))
            return;
        if (CountDiacritics(specMarkdown) < SpecDiacriticsForVietnamese)
            return;
        if (CountDiacritics(visible) >= PocDiacriticsFloor)
            return;

        issues.Add(
            "AI Design Spec viết bằng TIẾNG VIỆT nhưng chữ trên POC (nhãn, nút, tiêu đề cột, dữ liệu mẫu) gần như không có dấu tiếng Việt — UI đang ở ngôn ngữ khác. "
            + "Dịch toàn bộ phần hiển thị sang tiếng Việt đúng như spec: người dùng nghiệp vụ nhận demo bằng ngôn ngữ lạ thì không đọc được, dù chức năng có đủ.");
    }

    private static bool HasUsableSample(string? realSampleText) =>
        ExtractTokens(realSampleText).Count >= MinTokensToJudge;

    /// <summary>
    /// Các "token đặc trưng" của tài liệu thật: mỗi ô/dòng sau khi tách theo xuống dòng, dấu <c>|</c>
    /// (bảng Word/Excel bóc ra), tab và dấu chấm phẩy. Chỉ giữ token đủ dài (từ ngắn phổ thông có mặt
    /// trong mọi POC nên không chứng minh được gì), có chữ cái và không phải dòng tiêu đề của trình bóc.
    /// </summary>
    public static IReadOnlyList<string> ExtractTokens(string? realSampleText)
    {
        if (string.IsNullOrWhiteSpace(realSampleText))
            return Array.Empty<string>();

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in realSampleText.Split(['\n', '\r', '|', '\t', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().Trim('"', '\'', '*', '-', ':', ',', '.');
            if (token.Length is < MinTokenLength or > MaxTokenLength)
                continue;
            // Dòng chú thích của trình bóc text ("[Trích từ danh-muc.xlsx]", "[Sheet1]", "…(đã cắt bớt)").
            if (token.StartsWith('[') || token.StartsWith('…'))
                continue;
            if (!token.Any(char.IsLetter))
                continue;
            if (!seen.Add(token))
                continue;

            tokens.Add(token);
            if (tokens.Count >= MaxTokens)
                break;
        }

        return tokens;
    }

    // Chữ NGƯỜI DÙNG NHÌN THẤY: bỏ thẻ (kèm mọi attribute) rồi giải mã entity. Attribute cố tình bị bỏ —
    // một `data-view="Đăng nhập"` không chứng minh nhãn hiển thị là tiếng Việt.
    private static string VisibleText(string markup)
    {
        var stripped = TagRegex().Replace(markup, " ");
        return WebUtility.HtmlDecode(WhitespaceRegex().Replace(stripped, " ")).Trim();
    }

    private static int CountDiacritics(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (VietnameseDiacritics.Contains(char.ToLowerInvariant(c)))
                count++;
        }
        return count;
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\b(?:Nguy[eễ]n|Tr[aầ]n|L[eê]|Ph[aạ]m|Ho[aà]ng)\s+(?:V[aă]n|Th[iị])\s+[A-DX](?![\p{L}])", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderPersonViRegex();

    [GeneratedRegex(@"\b(?:S[aả]n ph[aẩ]m|Kh[aá]ch h[aà]ng|[DĐ][oơ]n h[aà]ng|Nh[aâ]n vi[eê]n|Ph[oò]ng ban|Danh m[uụ]c|V[aậ]t t[uư])\s+(?:[A-DX]|0?[1-3])(?![\p{L}\d])", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderEntityViRegex();

    [GeneratedRegex(@"\bC[oô]ng ty\s+(?:ABC|XYZ|A|B)(?![\p{L}])", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderCompanyViRegex();

    [GeneratedRegex(@"\b(?:John|Jane)\s+Doe\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderPersonEnRegex();

    [GeneratedRegex(@"\b(?:Item|Product|Customer|Order|Employee|Sample)\s+(?:[A-DX]|0?[1-3])(?![\p{L}\d])", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderEntityEnRegex();

    [GeneratedRegex(@"\bLorem ipsum\b", RegexOptions.IgnoreCase)]
    private static partial Regex LoremRegex();

    [GeneratedRegex(@"[\w.+-]+@(?:example\.(?:com|org)|test\.(?:com|vn)|abc\.com)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderEmailRegex();
}
