using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Tiến độ của panel "Tiến độ khai thác". MẪU SỐ LÀ TỔNG SỐ NHÓM, không phải số nhóm áp dụng: nhóm bị
/// đánh [KHÔNG ÁP DỤNG] giữa chừng mà rút khỏi mẫu số thì con số đang chạy tự nhảy lùi mốc ("0/12" →
/// "3/9") và người dùng không hiểu vì sao thước đo đổi.
/// </summary>
/// <param name="Clear">Số nhóm đã [RÕ] — tử số của dòng chữ.</param>
/// <param name="Applicable">Số nhóm còn áp dụng (bỏ [KHÔNG ÁP DỤNG]).</param>
/// <param name="Total">Tổng số nhóm của bản đồ (12 với bản đồ đủ) — mẫu số của dòng chữ.</param>
public readonly record struct CoverageProgress(int Clear, int Applicable, int Total)
{
    /// <summary>Số nhóm đã được loại khỏi phạm vi dự án ([KHÔNG ÁP DỤNG]).</summary>
    public int NotApplicable => Total - Applicable;

    /// <summary>
    /// Phần trăm cho thanh tiến độ. Nhóm [KHÔNG ÁP DỤNG] tính là ĐÃ XONG — chúng không bao giờ lên [RÕ]
    /// được, nên nếu không tính thì thanh không bao giờ đầy trong khi cổng readiness (mọi dòng áp dụng
    /// [RÕ] — xem <see cref="RequirementReadinessGate"/>) đã mở nút "Write Requirement". Bất biến
    /// "thanh đầy ⇔ nút mở khoá" là thứ cả UI lẫn tài liệu đang dựa vào, phải giữ.
    /// </summary>
    public int Percent => Total == 0 ? 0 : (Clear + NotApplicable) * 100 / Total;
}

/// <summary>
/// Đọc/ghi "Bản đồ bao phủ yêu cầu" của một dự án. Bản đồ được LƯU dưới dạng JSON
/// (<see cref="CoverageMapDocument"/>) và mọi tầng đọc nó qua <see cref="Parse"/> để lấy danh sách
/// <see cref="CoverageMapItem"/> — panel tiến độ, cổng readiness, các cổng bảng, và năm guard sửa bản đồ.
///
/// <para>
/// <b>Vì sao là JSON.</b> Bản đồ từng là 12 dòng bullet nhồi bốn trường vào một chuỗi
/// (<c>- ★ Nhãn: [TRẠNG THÁI] đã ghi nhận còn thiếu: phần hụt {nguồn: trích}</c>). Mọi tầng muốn sửa
/// một phần đều phải regex ra rồi ghép chuỗi lại — bốn guard làm đúng thế, mỗi cái tự dựng lại cờ ★ và
/// khối cuối dòng theo cách riêng, và một cái quên thì bản đồ mất một phần nội dung trong im lặng.
/// Trường bậc nhất khiến các guard chỉ còn gán thuộc tính; xem <see cref="CoverageMapItem"/>.
/// </para>
///
/// <para>
/// <b>Chỉ đọc JSON.</b> Đây là parser của bản đồ ĐÃ LƯU, nên nó không cần biết gì về văn xuôi model trả
/// về: lượt distill lấy JSON qua structured output, và nhánh dự phòng khi model không nhận
/// <c>response_format</c> nằm ở <c>RequirementCoverageService</c>, dùng <c>LlmJson</c> như mọi đường
/// "parse tay" khác của repo. Chiều ngược lại — dựng 12 dòng bullet cho prompt và bản xuất — là
/// <see cref="ToText"/>.
/// </para>
///
/// <para>
/// Chịu lỗi: JSON hỏng ⇒ danh sách rỗng (panel ẩn, cổng readiness báo "chưa tổng hợp được bản đồ").
/// Không ném lên khung chat.
/// </para>
/// </summary>
public static class CoverageMapParser
{
    /// <summary>
    /// Không escape non-ASCII: bản đồ toàn tiếng Việt, mà mặc định của System.Text.Json biến mỗi chữ có
    /// dấu thành <c>\uXXXX</c> — dài gấp ~6 lần và bản đồ này đi vào prompt ở MỌI lượt chat.
    /// </summary>
    /// <remarks>
    /// <see cref="CoverageKnownJsonConverter"/> nằm ở đây (chứ không phải một attribute trên contract) để
    /// bản đồ CŨ — thời <c>known</c> còn là một ô chuỗi — vẫn đọc được; xem lớp đó cho cái giá của việc
    /// không có nó. Cùng bộ tuỳ chọn dùng cho cả đọc lẫn ghi nên hai chiều không thể lệch nhau.
    /// </remarks>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new CoverageKnownJsonConverter() }
    };

    /// <summary>Đọc bản đồ đã lưu thành danh sách dòng. Rỗng/không đọc được ⇒ danh sách rỗng.</summary>
    public static IReadOnlyList<CoverageMapItem> Parse(string? coverageMap) =>
        string.IsNullOrWhiteSpace(coverageMap)
            ? Array.Empty<CoverageMapItem>()
            : ToItems(LlmJson.TryDeserialize<CoverageMapDocument>(coverageMap, options: SerializerOptions));

    /// <summary>
    /// Chuẩn hoá một <see cref="CoverageMapDocument"/> (đọc từ DB hoặc do structured output trả về) thành
    /// các dòng bản đồ: trim, chuẩn hoá trạng thái, bỏ dòng không có nhãn. Mở cho
    /// <c>RequirementCoverageService</c> để đường structured output và đường đọc DB dùng CHUNG một bộ
    /// chuẩn hoá — hai bản sao là hai thứ trôi lệch nhau.
    /// </summary>
    public static IReadOnlyList<CoverageMapItem> ToItems(CoverageMapDocument? doc)
    {
        if (doc?.Items == null)
            return Array.Empty<CoverageMapItem>();

        return doc.Items
            .Where(x => !string.IsNullOrWhiteSpace(x.Label))
            .Select(x => new CoverageMapItem
            {
                Label = x.Label.Trim(),
                IsCore = x.Core,
                Status = NormalizeStatus(x.Status),
                Known = (x.Known ?? new List<string>())
                    .Select(k => (k ?? string.Empty).Trim())
                    .Where(k => k.Length > 0)
                    .ToList()
            })
            .ToList();
    }

    /// <summary>Ghi danh sách dòng thành JSON để lưu vào <c>Project.RequirementCoverageMap</c>.</summary>
    public static string Serialize(IReadOnlyList<CoverageMapItem> items) =>
        JsonSerializer.Serialize(new CoverageMapDocument
        {
            Items = items.Select(x => new CoverageMapEntry
            {
                Label = x.Label,
                Core = x.IsCore,
                Status = x.Status,
                Known = x.Known.ToList()
            }).ToList()
        }, SerializerOptions);

    /// <summary>
    /// GẮN các câu hỏi MỞ vào đúng dòng của chúng — phép nối duy nhất giữa hai cột
    /// <c>RequirementCoverageMap</c> và <c>OpenQuestions</c>, và là thứ làm <see cref="CoverageMapItem.Summary"/>
    /// đọc lên y như thời bản đồ còn tự chở trường <c>nextQuestion</c>.
    /// <para>
    /// Gắn ở ĐƯỜNG ĐỌC chứ không lưu vào bản đồ: câu hỏi có vòng đời riêng (được đánh dấu đã trả lời, bị
    /// guard dọn) và một bản sao trong bản đồ là bản sao thứ hai sẽ trôi lệch — đúng thứ mà lần gộp hai
    /// lời gọi này vừa bỏ đi. Mục đã trả lời không bao giờ được gắn: dòng bản đồ chỉ hiện điều CÒN PHẢI HỎI.
    /// </para>
    /// Trả về chính danh sách đã nhận (đã sửa tại chỗ) để dùng được ngay trong một biểu thức.
    /// </summary>
    public static IReadOnlyList<CoverageMapItem> AttachQuestions(
        IReadOnlyList<CoverageMapItem> items, IEnumerable<OpenQuestionEntry>? questions)
    {
        var open = (questions ?? Enumerable.Empty<OpenQuestionEntry>())
            .Where(q => q.IsOpen && !string.IsNullOrWhiteSpace(q.Text))
            .ToList();

        foreach (var item in items)
        {
            item.Questions = open
                .Where(q => IsSameGroup(item.Label, q.Group))
                .Select(q => q.Text.Trim())
                .ToList();
        }

        return items;
    }

    /// <summary>
    /// Nhãn dòng bản đồ và nhóm của một câu hỏi có phải cùng một nhóm không. So khớp hai chiều bằng TIỀN
    /// TỐ: lượt distill viết *"Luồng ngoại lệ"* còn bản đồ ghi *"Luồng ngoại lệ &amp; trường hợp đặc biệt"*
    /// thì đó vẫn là một nhóm, và một phép so nguyên văn sẽ làm mọi guard câm trong im lặng. Nhóm rỗng
    /// (model đặt một tên lạ, đường ghi đã xoá về rỗng) KHÔNG khớp dòng nào — fail-open: câu hỏi ấy vẫn
    /// nằm trong ngữ cảnh chat để BA hỏi, chỉ không hạ được dòng bản đồ nào.
    /// <para>
    /// Là phép so DÙNG CHUNG của mọi tầng nối hai cột (bốn guard, cổng readiness, panel tiến độ) — bốn bản
    /// chép tay thì lần sửa sau chỉ sửa một bản.
    /// </para>
    /// </summary>
    public static bool IsSameGroup(string? label, string? group)
    {
        var left = (label ?? string.Empty).Trim();
        var right = (group ?? string.Empty).Trim();
        if (left.Length == 0 || right.Length == 0)
            return false;

        return left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dựng lại bản đồ ở dạng 12 dòng bullet cho NGƯỜI và cho MODEL đọc: ngữ cảnh chat của BA, bản xuất
    /// hội thoại. JSON là format lưu trữ vì nó sửa được từng trường, nhưng nhét dấu ngoặc nhọn vào prompt
    /// chat thì vừa tốn token vừa mời model chép lại cú pháp JSON ra câu trả lời cho người dùng.
    /// <para>
    /// Các mẩu đã ghi nhận ngăn bằng <see cref="CoverageMapItem.KnownSeparator"/>, KHÁC với văn xuôi mà
    /// <see cref="CoverageMapItem.Summary"/> dựng cho người dùng đọc: ở đây người đọc chính là model của
    /// lượt sau, và nó cần thấy đúng ranh giới từng ý để gộp mà không nuốt mất ý nào.
    /// </para>
    /// </summary>
    public static string ToText(IReadOnlyList<CoverageMapItem> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.Append("- ");
            if (item.IsCore)
                sb.Append("★ ");
            sb.Append(item.Label).Append(": [").Append(item.Status).Append(']');

            // Các mẩu đã ghi nhận ngăn bằng KnownSeparator chứ không nối trần: model đọc khối này để
            // gộp lượt kế tiếp, nên nó phải thấy được ranh giới từng ý — nối trần thì lượt sau gộp hai ý
            // thành một câu và một trong hai biến mất. Cũng là thứ làm khối này tách lại được (fixture của
            // test là phép nghịch đảo của hàm này).
            var known = string.Join(CoverageMapItem.KnownSeparator, item.Known.Where(k => !string.IsNullOrWhiteSpace(k)));
            var questions = string.Join("; ", item.Questions.Where(q => !string.IsNullOrWhiteSpace(q)));

            if (known.Length > 0)
                sb.Append(' ').Append(known);
            if (questions.Length > 0)
                sb.Append(' ').Append(CoverageMapItem.OpenQuestionMarker).Append(' ').Append(questions);

            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Tiến độ khai thác của bản đồ — cho thanh + dòng "Đã rõ x/y nhóm" của panel.</summary>
    public static CoverageProgress Progress(IReadOnlyList<CoverageMapItem> items) => new(
        Clear: items.Count(x => x.Status == "RÕ"),
        Applicable: items.Count(x => x.Status != "KHÔNG ÁP DỤNG"),
        Total: items.Count);

    /// <summary>Chuẩn hoá tên trạng thái của một dòng; giá trị lạ ⇒ [CHƯA HỎI].</summary>
    private static string NormalizeStatus(string? raw)
    {
        var status = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return status switch
        {
            "RÕ" or "RO" => "RÕ",
            "MỘT PHẦN" or "MOT PHAN" => "MỘT PHẦN",
            "KHÔNG ÁP DỤNG" or "KHONG AP DUNG" => "KHÔNG ÁP DỤNG",
            _ => "CHƯA HỎI"
        };
    }
}
