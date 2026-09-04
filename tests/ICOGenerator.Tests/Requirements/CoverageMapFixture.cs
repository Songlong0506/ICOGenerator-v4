using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Tests.Requirements;

/// <summary>
/// Dựng bản đồ bao phủ cho test từ dạng 12 dòng bullet mà con người đọc được —
/// <c>- ★ Nhãn: [TRẠNG THÁI] đã ghi nhận còn thiếu: phần hụt {nguồn: trích}</c> — rồi trả về JSON đúng
/// như thứ được lưu trong <c>Project.RequirementCoverageMap</c>.
///
/// <para>
/// <b>Một dòng bullet chở HAI thứ nay được lưu ở HAI cột.</b> Phần trước <c>còn thiếu:</c> là dòng bản đồ
/// (<see cref="Map"/>), phần sau là một CÂU HỎI của nhóm ấy (<see cref="Questions"/>, lưu ở
/// <c>Project.OpenQuestions</c>). Fixture giữ nguyên một dòng cho cả hai vì đó vẫn là cách đọc tự nhiên
/// nhất khi soi một test: nhóm này đang ở trạng thái gì, đã ghi nhận gì, và còn phải hỏi gì.
/// Nhiều câu hỏi cho cùng một nhóm thì ngăn bằng dấu <c>;</c> — đúng cách
/// <see cref="CoverageMapItem.Summary"/> ghép chúng lại.
/// </para>
///
/// <para>
/// <b>Đây là DSL của test, không phải một format thứ hai của hệ thống.</b> Production chỉ đọc và ghi
/// JSON. Nhưng dạng bullet vẫn sống ở chiều ngược lại — <see cref="CoverageMapParser.ToText"/> dựng đúng
/// nó cho ngữ cảnh chat của BA và bản xuất hội thoại — nên viết fixture bằng nó là viết bằng đúng thứ
/// người đọc test cần thấy: một bản đồ JSON dán thẳng vào file test thì không ai soi ra được dòng nào
/// đang <c>[MỘT PHẦN]</c> vì lý do gì.
/// </para>
///
/// <para>
/// Hàm này là NGHỊCH ĐẢO của <see cref="CoverageMapParser.ToText"/>, và
/// <c>CoverageMapFixtureTests</c> chốt hai chiều khớp nhau — fixture trôi khỏi format thật thì fail ở đó
/// chứ không âm thầm làm cả chục test khác kiểm sai thứ.
/// </para>
/// </summary>
public static class CoverageMapFixture
{
    /// <summary>Bản đồ JSON dựng từ các dòng bullet. Dòng không đúng dạng bị bỏ qua.</summary>
    public static string Map(string bulletText) =>
        CoverageMapParser.Serialize(Items(bulletText));

    /// <summary>
    /// Danh sách câu hỏi dựng từ chính các dòng bullet ấy: mỗi mẩu sau <c>còn thiếu:</c> thành một mục
    /// <c>MỞ</c> gắn nhãn nhóm của dòng. Đây là nửa còn lại của một bản đồ trong test — cổng readiness và
    /// các guard đều nhận cả hai.
    /// </summary>
    public static List<OpenQuestionEntry> Questions(string bulletText) =>
        Items(bulletText)
            .SelectMany(item => item.Questions.Select(q => new OpenQuestionEntry { Group = item.Label, Text = q }))
            .ToList();

    /// <summary>Chuỗi JSON đúng như thứ được lưu trong <c>Project.OpenQuestions</c>.</summary>
    public static string? StoredQuestions(string bulletText) =>
        InterviewOutlookParser.SerializeOpenQuestions(Questions(bulletText));

    /// <summary>
    /// Lời đáp của MỘT lượt distill — bản đồ + danh sách câu hỏi trong cùng một object, đúng hình dạng
    /// <see cref="CoverageDistillDocument"/> mà <c>RequirementCoverageService</c> đọc.
    /// </summary>
    public static string DistillReply(string bulletText)
    {
        var items = Items(bulletText);
        return JsonSerializer.Serialize(new CoverageDistillDocument
        {
            Items = items.Select(x => new CoverageMapEntry
            {
                Label = x.Label, Core = x.IsCore, Status = x.Status, Known = x.Known, Evidence = x.Evidence
            }).ToList(),
            Questions = Questions(bulletText)
        }, ReplyOptions);
    }

    private static readonly JsonSerializerOptions ReplyOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Bản đồ <paramref name="map"/> với các dòng trong <paramref name="bulletLines"/> ghi đè lên dòng
    /// CÙNG NHÃN. Thay cho lối cũ <c>Map.Replace(nguyên_văn_dòng_cũ, dòng_mới)</c>: phép thế chuỗi bắt
    /// test chép lại y hệt cả dòng cũ kèm khối <c>{nguồn: …}</c>, nên chỉ cần sửa một dấu phẩy ở fixture
    /// gốc là phép thế lặng lẽ không khớp và test kiểm nhầm một bản đồ chưa đổi gì.
    /// Nhãn không có trong bản đồ ⇒ thêm dòng mới vào cuối.
    /// </summary>
    public static string With(string map, params string[] bulletLines)
    {
        var items = CoverageMapParser.Parse(map).ToList();
        foreach (var line in bulletLines)
        {
            foreach (var replacement in Items(line))
            {
                var at = items.FindIndex(x => string.Equals(x.Label, replacement.Label, StringComparison.Ordinal));
                if (at < 0)
                    items.Add(replacement);
                else
                    items[at] = replacement;
            }
        }
        return CoverageMapParser.Serialize(items);
    }

    /// <summary>Các dòng bản đồ đọc từ dạng bullet — cho test cần so trực tiếp trên item.</summary>
    public static IReadOnlyList<CoverageMapItem> Items(string bulletText)
    {
        var items = new List<CoverageMapItem>();
        foreach (var raw in (bulletText ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var match = LineRegex.Match(raw.Trim());
            if (!match.Success)
                continue;

            var summary = match.Groups["summary"].Value.Trim();

            var evidence = string.Empty;
            var evidenceMatch = EvidenceRegex.Match(summary);
            if (evidenceMatch.Success)
            {
                evidence = evidenceMatch.Groups["evidence"].Value.Trim();
                summary = summary[..evidenceMatch.Index].Trim();
            }

            var known = summary;
            var questions = Array.Empty<string>();
            var at = summary.IndexOf(CoverageMapItem.OpenQuestionMarker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                known = summary[..at].Trim();
                questions = summary[(at + CoverageMapItem.OpenQuestionMarker.Length)..]
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            items.Add(new CoverageMapItem
            {
                IsCore = match.Groups["core"].Success,
                Label = match.Groups["label"].Value.Trim(),
                Status = match.Groups["status"].Value.Trim(),
                Known = known,
                Questions = questions,
                Evidence = evidence
            });
        }

        return items;
    }

    private static readonly Regex LineRegex =
        new(@"^-\s*(?<core>★)?\s*(?<label>[^:\[\]]+):\s*\[(?<status>[^\]]+)\]\s*(?<summary>.*)$");

    private static readonly Regex EvidenceRegex =
        new(@"\{\s*(?:nguồn|nguon|source)\s*:\s*(?<evidence>[^}]*)\}\s*$", RegexOptions.IgnoreCase);
}
