using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;

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
/// <see cref="CoverageMapItem"/> — panel tiến độ, cổng readiness, các cổng bảng, và bốn guard sửa bản đồ.
///
/// <para>
/// <b>Vì sao là JSON.</b> Bản đồ từng là 12 dòng bullet nhồi bốn trường vào một chuỗi
/// (<c>- ★ Nhãn: [TRẠNG THÁI] đã ghi nhận còn thiếu: phần hụt {nguồn: trích}</c>). Mọi tầng muốn sửa
/// một phần đều phải regex ra rồi ghép chuỗi lại — bốn guard làm đúng thế, mỗi cái tự dựng lại cờ ★ và
/// khối <c>{nguồn: …}</c> theo cách riêng, và một cái quên thì bản đồ mất bằng chứng trong im lặng.
/// Trường bậc nhất khiến các guard chỉ còn gán thuộc tính; xem <see cref="CoverageMapItem"/>.
/// </para>
///
/// <para>
/// <b>Tương thích ngược, không cần migration.</b> Dự án tạo trước lần đổi format còn giữ bản đồ dạng
/// text trong DB. <see cref="Parse"/> tự nhận dạng: chuỗi mở đầu bằng <c>{</c> là JSON, còn lại đọc bằng
/// đường text cũ (<see cref="ParseLegacyText"/>) — cùng regex, cùng cách tách <c>còn thiếu:</c> và
/// <c>{nguồn: …}</c> như trước. Lượt distill kế tiếp ghi đè bằng JSON, nên bản đồ cũ tự chuyển dần sang
/// format mới mà không có bước migration nào chạm vào DB.
/// </para>
///
/// <para>
/// Chịu lỗi ở mọi đường: JSON hỏng / text không đúng dạng ⇒ bỏ qua phần không đọc được, map rỗng ⇒ danh
/// sách rỗng (panel ẩn, cổng readiness báo "chưa tổng hợp được bản đồ"). Không ném lên khung chat.
/// </para>
/// </summary>
public static partial class CoverageMapParser
{
    /// <summary>
    /// Không escape non-ASCII: bản đồ toàn tiếng Việt, mà mặc định của System.Text.Json biến mỗi chữ có
    /// dấu thành <c>\uXXXX</c> — dài gấp ~6 lần và bản đồ này đi vào prompt ở MỌI lượt chat.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>
    /// Đọc bản đồ (JSON, hoặc text của format cũ) thành danh sách dòng. Rỗng/không đọc được ⇒ danh sách rỗng.
    /// </summary>
    public static IReadOnlyList<CoverageMapItem> Parse(string? coverageMap)
    {
        if (string.IsNullOrWhiteSpace(coverageMap))
            return Array.Empty<CoverageMapItem>();

        var text = coverageMap.Trim();
        return text.StartsWith('{') ? ParseJson(text) : ParseLegacyText(text);
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
                Known = x.Known,
                Gap = x.Gap,
                Evidence = x.Evidence
            }).ToList()
        }, SerializerOptions);

    /// <summary>
    /// Dựng lại bản đồ ở dạng 12 dòng bullet cho NGƯỜI và cho MODEL đọc: ngữ cảnh chat của BA, bản xuất
    /// hội thoại. JSON là format lưu trữ vì nó sửa được từng trường, nhưng nhét dấu ngoặc nhọn vào prompt
    /// chat thì vừa tốn token vừa mời model chép lại cú pháp JSON ra câu trả lời cho người dùng.
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

            if (!string.IsNullOrWhiteSpace(item.Summary))
                sb.Append(' ').Append(item.Summary);
            if (!string.IsNullOrWhiteSpace(item.Evidence))
                sb.Append(" {nguồn: ").Append(item.Evidence).Append('}');

            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Tiến độ khai thác của bản đồ — cho thanh + dòng "Đã rõ x/y nhóm" của panel.</summary>
    public static CoverageProgress Progress(IReadOnlyList<CoverageMapItem> items) => new(
        Clear: items.Count(x => x.Status == "RÕ"),
        Applicable: items.Count(x => x.Status != "KHÔNG ÁP DỤNG"),
        Total: items.Count);

    private static IReadOnlyList<CoverageMapItem> ParseJson(string json)
    {
        CoverageMapDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<CoverageMapDocument>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            // Bản đồ hỏng cú pháp (model bị cắt giữa chừng, ghi lỗi) ⇒ coi như chưa có bản đồ. Cùng cách
            // fail-open với text cũ: panel ẩn, cổng readiness nói chưa tổng hợp được, lượt sau gộp bù.
            return Array.Empty<CoverageMapItem>();
        }

        if (doc?.Items == null)
            return Array.Empty<CoverageMapItem>();

        return doc.Items
            .Where(x => !string.IsNullOrWhiteSpace(x.Label))
            .Select(x => new CoverageMapItem
            {
                Label = x.Label.Trim(),
                IsCore = x.Core,
                Status = NormalizeStatus(x.Status),
                Known = (x.Known ?? string.Empty).Trim(),
                Gap = (x.Gap ?? string.Empty).Trim(),
                Evidence = (x.Evidence ?? string.Empty).Trim()
            })
            .ToList();
    }

    /// <summary>
    /// Đường TƯƠNG THÍCH NGƯỢC: đọc bản đồ dạng text của format cũ còn nằm trong DB. Giữ nguyên cách đọc
    /// trước đây — bóc <c>{nguồn: …}</c> ở cuối, rồi tách phần đã ghi nhận với phần <c>còn thiếu:</c> —
    /// nên một bản đồ cũ cho ra đúng các dòng như trước lần đổi format.
    /// </summary>
    private static IReadOnlyList<CoverageMapItem> ParseLegacyText(string coverageMap)
    {
        var items = new List<CoverageMapItem>();
        foreach (var raw in coverageMap.Replace("\r\n", "\n").Split('\n'))
        {
            var match = CoverageLineRegex().Match(raw.Trim());
            if (!match.Success)
                continue;

            var (summary, evidence) = SplitEvidence(match.Groups["summary"].Value.Trim());
            var (known, gap) = SplitGap(summary);

            items.Add(new CoverageMapItem
            {
                IsCore = match.Groups["core"].Success,
                Label = match.Groups["label"].Value.Trim(),
                Status = NormalizeStatus(match.Groups["status"].Value),
                Known = known,
                Gap = gap,
                Evidence = evidence
            });
        }

        return items;
    }

    /// <summary>
    /// Tách khối bằng chứng "{nguồn: …}" ở CUỐI tóm tắt của format text cũ. Không có khối ⇒ trả nguyên
    /// tóm tắt + bằng chứng rỗng.
    /// </summary>
    public static (string Summary, string Evidence) SplitEvidence(string summary)
    {
        var match = EvidenceRegex().Match(summary ?? string.Empty);
        return match.Success
            ? (summary![..match.Index].Trim(), match.Groups["evidence"].Value.Trim())
            : (summary ?? string.Empty, string.Empty);
    }

    /// <summary>
    /// Tách phần đã ghi nhận với phần "còn thiếu: …" trong tóm tắt của format text cũ. Không có dấu ngăn
    /// ⇒ tất cả là phần đã ghi nhận.
    /// </summary>
    public static (string Known, string Gap) SplitGap(string summary)
    {
        var text = (summary ?? string.Empty).Trim();
        var at = text.IndexOf(CoverageMapItem.GapMarker, StringComparison.OrdinalIgnoreCase);
        return at < 0
            ? (text, string.Empty)
            : (text[..at].Trim(), text[(at + CoverageMapItem.GapMarker.Length)..].Trim());
    }

    /// <summary>Chuẩn hoá tên trạng thái của một dòng; giá trị lạ ⇒ [CHƯA HỎI].</summary>
    internal static string NormalizeStatus(string? raw)
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

    // "- ★ Mục tiêu / bài toán: [RÕ] tóm tắt…" — format CŨ, chỉ còn dùng ở đường tương thích ngược.
    [GeneratedRegex(@"^-\s*(?<core>★)?\s*(?<label>[^:\[\]]+):\s*\[(?<status>[^\]]+)\]\s*(?<summary>.*)$")]
    private static partial Regex CoverageLineRegex();

    // Khối bằng chứng ở cuối tóm tắt: "{nguồn: người dùng nói 'quản lý duyệt là xong'}".
    [GeneratedRegex(@"\{\s*(?:nguồn|nguon|source)\s*:\s*(?<evidence>[^}]*)\}\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EvidenceRegex();
}
