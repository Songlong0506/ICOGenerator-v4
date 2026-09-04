using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Đọc/ghi hai danh sách của cuộc phỏng vấn — <c>Project.OpenQuestions</c> (câu hỏi, ghi cùng bản đồ bao
/// phủ ngay TRONG lượt chat) và <c>Project.WorkedExamples</c> (ví dụ đã xác nhận, chắt ở HẬU KỲ). Cả hai
/// LƯU dưới dạng JSON (<see cref="OpenQuestionDocument"/> / <see cref="WorkedExampleDocument"/>) và mọi
/// tầng đọc chúng qua lớp này. Cùng vai trò với <see cref="CoverageMapParser"/> đối với bản đồ bao phủ.
///
/// <para>
/// <b>JSON để lưu, bullet để nạp prompt.</b> Đúng cái split mà bản đồ bao phủ đã dùng
/// (<see cref="CoverageMapParser.ToText"/>): nhét dấu ngoặc nhọn vào ngữ cảnh chat vừa tốn token vừa mời
/// model chép lại cú pháp JSON ra câu trả lời cho người dùng. Có HAI cách dựng bullet vì hai loại người
/// đọc: <see cref="ToText(IEnumerable{OpenQuestionEntry})"/> chỉ in mục còn MỞ và bỏ hẳn nhãn nhóm (BA và
/// bước soạn Brief — nhãn là từ vựng NỘI BỘ của bản đồ, prompt chat cấm ném nó vào mặt người dùng nghiệp
/// vụ), còn <see cref="ToTaggedText"/> in TOÀN BỘ kèm nhãn và dấu trạng thái cho ĐÚNG một chỗ: khối
/// "trạng thái hiện có" echo lại cho chính lượt chắt lọc, nơi model cần thấy cặp nhóm↔câu hỏi để giữ
/// nguyên nhóm của mục cũ và cần thấy mục đã đóng để không dựng lại nó.
/// </para>
///
/// <para>
/// <b>Đọc được cả bản ghi format CŨ.</b> Đây là điểm khác có chủ ý so với <see cref="CoverageMapParser"/>
/// ("chỉ đọc JSON"), và lý do nằm ở cột <c>WorkedExamples</c>: nó chỉ được ghi bởi lượt chắt lọc HẬU KỲ
/// CHAT, nên một dự án đã phỏng vấn xong và đang ở bước sinh AI Design Spec sẽ không bao giờ có lượt chat
/// nào nữa — đọc hụt ở đó là mất VĨNH VIỄN oracle mà POC bị chấm theo, đúng kiểu mất-trong-im-lặng mà cả
/// tầng guard này sinh ra để chặn. Danh sách câu hỏi đi cùng nhánh đọc ấy vì hai cột dùng chung một lớp.
/// Nhánh dưới chỉ ĐỌC, không ai ghi ra nữa: nó tự cạn khi các dự án cũ đi qua lượt chat kế tiếp.
/// </para>
/// </summary>
public static partial class InterviewOutlookParser
{
    /// <summary>
    /// Trần độ dài chuỗi lưu mỗi cột — hai danh sách này đi vào prompt ở nhiều bước, không phải biên bản.
    /// </summary>
    private const int MaxCharsPerList = 4000;

    /// <summary>
    /// Không escape non-ASCII, cùng lý do với <see cref="CoverageMapParser"/>: hai danh sách này toàn
    /// tiếng Việt, mà mặc định của System.Text.Json biến mỗi chữ có dấu thành <c>\uXXXX</c>.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>Đọc "Điểm cần làm rõ" đã lưu. Rỗng/không đọc được ⇒ danh sách rỗng.</summary>
    public static IReadOnlyList<OpenQuestionEntry> ParseOpenQuestions(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return Array.Empty<OpenQuestionEntry>();

        // requireKnownProperty: một dòng bullet cũ có chứa dấu ngoặc nhọn vẫn bóc ra được "JSON", và
        // System.Text.Json vui vẻ biến nó thành một document rỗng — tức nuốt mất nhánh đọc format cũ.
        var doc = LlmJson.TryDeserialize<OpenQuestionDocument>(stored, requireKnownProperty: true);
        return doc?.Items != null ? ToOpenQuestions(doc.Items) : LegacyOpenQuestions(stored);
    }

    /// <summary>Đọc "Ví dụ đã xác nhận" đã lưu. Rỗng/không đọc được ⇒ danh sách rỗng.</summary>
    public static IReadOnlyList<string> ParseWorkedExamples(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return Array.Empty<string>();

        var doc = LlmJson.TryDeserialize<WorkedExampleDocument>(stored, requireKnownProperty: true);
        return doc?.Items != null ? Clean(doc.Items) : Clean(LegacyBullets(stored));
    }

    /// <summary>
    /// Chuẩn hoá danh sách điểm cần làm rõ (đọc từ DB hoặc do structured output trả về): trim, bỏ mục
    /// không có nội dung. Mở cho <see cref="InterviewOutlookService"/> để đường structured output và
    /// đường đọc DB dùng CHUNG một bộ chuẩn hoá — hai bản sao là hai thứ trôi lệch nhau.
    /// </summary>
    public static IReadOnlyList<OpenQuestionEntry> ToOpenQuestions(IEnumerable<OpenQuestionEntry>? items)
    {
        if (items == null)
            return Array.Empty<OpenQuestionEntry>();

        return items
            .Where(x => !string.IsNullOrWhiteSpace(x?.Text))
            .Select(x => new OpenQuestionEntry
            {
                Group = (x.Group ?? string.Empty).Trim(),
                Text = x.Text.Trim(),
                Status = NormalizeStatus(x.Status),
                Answer = (x.Answer ?? string.Empty).Trim()
            })
            .ToList();
    }

    /// <summary>
    /// Chuẩn hoá trạng thái một mục; giá trị lạ ⇒ <see cref="OpenQuestionEntry.Open"/>. Fail-open có chủ
    /// ý và ngược chiều với <c>CoverageMapParser.NormalizeStatus</c> (giá trị lạ ⇒ [CHƯA HỎI], tức chặn
    /// cổng): ở đây đọc hụt một trạng thái chỉ tốn thêm một câu hỏi, còn đọc nhầm thành "đã trả lời" là
    /// im lặng bỏ mất một điểm chưa ai chốt — mà không tầng nào phía sau bắt được nữa.
    /// </summary>
    private static string NormalizeStatus(string? raw)
    {
        var status = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return status is "ĐÃ TRẢ LỜI" or "DA TRA LOI" ? OpenQuestionEntry.Answered : OpenQuestionEntry.Open;
    }

    /// <summary>
    /// Số mục ĐÃ TRẢ LỜI được giữ lại — các mục MỚI NHẤT. Chúng chỉ còn một việc: nằm trong khối echo để
    /// lượt chắt lọc kế tiếp không dựng lại một câu hỏi đã đóng. Giữ vô hạn thì danh sách phình mãi trong
    /// prompt và, tệ hơn, đẩy chính các mục còn MỞ ra khỏi trần độ dài bên dưới.
    /// </summary>
    private const int MaxAnsweredKept = 15;

    /// <summary>
    /// Ghi danh sách câu hỏi thành JSON để lưu vào <c>Project.OpenQuestions</c>.
    /// <para>
    /// Mục còn MỞ đứng TRƯỚC, mục đã trả lời đứng sau — vì <see cref="Fit"/> cắt từ CUỐI khi vượt trần độ
    /// dài. Thứ tự này biến trần đó thành "bỏ mục đã đóng trước", chứ không phải "bỏ mất một điểm chưa ai
    /// chốt" — mất một câu hỏi còn treo là mất trong im lặng, đúng loại hỏng mà cả tầng guard của bản đồ
    /// sinh ra để chặn.
    /// </para>
    /// </summary>
    public static string? SerializeOpenQuestions(IReadOnlyList<OpenQuestionEntry> items)
    {
        var clean = ToOpenQuestions(items);
        var ordered = clean.Where(x => x.IsOpen)
            .Concat(clean.Where(x => !x.IsOpen).TakeLast(MaxAnsweredKept))
            .ToList();

        return Fit(ordered.Count, take => JsonSerializer.Serialize(
            new OpenQuestionDocument { Items = ordered.Take(take).ToList() }, SerializerOptions));
    }

    /// <summary>Ghi danh sách ví dụ thành JSON để lưu vào <c>Project.WorkedExamples</c>.</summary>
    public static string? SerializeWorkedExamples(IReadOnlyList<string> items)
    {
        var clean = Clean(items);
        return Fit(clean.Count, take => JsonSerializer.Serialize(
            new WorkedExampleDocument { Items = clean.Take(take).ToList() }, SerializerOptions));
    }

    /// <summary>
    /// Các câu hỏi CÒN MỞ ở dạng bullet, KHÔNG kèm nhãn nhóm — cho BA và bước soạn Brief đọc. Mục đã trả
    /// lời bị bỏ ở đây: nạp chúng vào ngữ cảnh chat là mời BA hỏi lại đúng điều người dùng đã nói.
    /// </summary>
    public static string ToText(IEnumerable<OpenQuestionEntry> items)
        => Bullets(items.Where(x => x.IsOpen).Select(x => x.Text));

    /// <summary>Các ví dụ ở dạng bullet.</summary>
    public static string ToText(IEnumerable<string> items) => Bullets(items);

    /// <summary>
    /// TOÀN BỘ danh sách ở dạng bullet CÓ nhãn nhóm và có dấu trạng thái — chỉ dùng cho khối "trạng thái
    /// hiện có" echo lại cho chính lượt chắt lọc, xem doc của class. Mục không có nhóm in ra không kèm thẻ.
    /// <para>
    /// Đây là chỗ DUY NHẤT mục ĐÃ TRẢ LỜI được in ra, và đó là lý do chúng còn nằm trong danh sách: model
    /// phải thấy câu nào đã đóng thì mới không dựng lại nó ở lượt sau.
    /// </para>
    /// </summary>
    public static string ToTaggedText(IEnumerable<OpenQuestionEntry> items)
        => Bullets(items.Select(x =>
        {
            var head = string.IsNullOrWhiteSpace(x.Group) ? x.Text : $"[{x.Group}] {x.Text}";
            if (x.IsOpen)
                return head;

            return string.IsNullOrWhiteSpace(x.Answer)
                ? $"{head} → [{OpenQuestionEntry.Answered}]"
                : $"{head} → [{OpenQuestionEntry.Answered}] {x.Answer}";
        }));

    private static string Bullets(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
            sb.Append("- ").Append(line.Trim()).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    private static List<string> Clean(IEnumerable<string?> items)
        => items.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToList();

    /// <summary>
    /// Serialize <paramref name="count"/> mục đầu, BỚT DẦN từ cuối cho tới khi vừa trần độ dài.
    /// <para>
    /// Format cũ cắt cụt theo KÝ TỰ (<c>text[..4000]</c>) — với bullet thì mất mục cuối, với JSON thì mất
    /// SẠCH: một document bị cắt giữa chuỗi không parse lại được, tức trần độ dài tự biến thành một cái
    /// bẫy xoá trắng cả danh sách. Bớt theo MỤC giữ nguyên ý định của trần (chặn phình prompt) mà không
    /// bao giờ sinh ra JSON hỏng. Một mục đơn lẻ dài quá trần thì vẫn được giữ: cột là
    /// <c>nvarchar(max)</c>, và trả về rỗng ở đây là đúng cái mất-trong-im-lặng vừa nói.
    /// </para>
    /// </summary>
    private static string? Fit(int count, Func<int, string> serializeFirst)
    {
        if (count == 0)
            return null;

        for (var take = count; take > 1; take--)
        {
            var json = serializeFirst(take);
            if (json.Length <= MaxCharsPerList)
                return json;
        }
        return serializeFirst(1);
    }

    // ------------------------------------------------------------------------------------------------
    // FORMAT CŨ — chỉ đọc. Xem doc của class cho lý do nhánh này còn sống.
    // ------------------------------------------------------------------------------------------------

    private static IReadOnlyList<OpenQuestionEntry> LegacyOpenQuestions(string stored)
    {
        var items = new List<OpenQuestionEntry>();
        foreach (var line in LegacyBullets(stored))
        {
            var match = LegacyTaggedItemRegex().Match(line);
            items.Add(match.Success
                ? new OpenQuestionEntry { Group = match.Groups["group"].Value.Trim(), Text = match.Groups["text"].Value.Trim() }
                : new OpenQuestionEntry { Text = line });
        }
        return ToOpenQuestions(items);
    }

    /// <summary>Tách text bullet (mỗi dòng "- …") thành danh sách; rỗng → danh sách rỗng.</summary>
    private static List<string> LegacyBullets(string stored)
        => stored.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..].Trim())
            .Where(l => l.Length > 0)
            .ToList();

    // "[Vòng đời & trạng thái] Chưa rõ kết quả Complete dùng để chuyển bước nào" — khuôn thẻ nhóm mà
    // interview-outlook.v1.md từng bắt model tự gõ ở ĐẦU mỗi mục.
    [GeneratedRegex(@"^\[(?<group>[^\]]{1,80})\]\s*(?<text>.+)$", RegexOptions.Singleline)]
    private static partial Regex LegacyTaggedItemRegex();
}
