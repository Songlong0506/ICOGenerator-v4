using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Tests.Requirements;

/// <summary>
/// Dựng danh sách "Điểm cần làm rõ còn tồn đọng" cho test từ khuôn đọc được
/// <c>[Nhóm] câu hỏi</c>, rồi trả về <see cref="OpenQuestionEntry"/> — thứ mà production đọc.
///
/// <para>
/// <b>Đây là DSL của test, không phải một format thứ hai của hệ thống.</b> Cùng vai trò với
/// <see cref="CoverageMapFixture"/>: production lưu JSON và mọi tầng đọc trường bậc nhất, nhưng khuôn
/// <c>[Nhóm] câu hỏi</c> vẫn sống ở chiều ngược lại (<see cref="InterviewOutlookParser.ToTaggedText"/>
/// dựng đúng nó cho khối "trạng thái hiện có" của lượt chắt lọc), nên viết fixture bằng nó là viết bằng
/// đúng thứ người đọc test cần thấy — cặp nhóm↔câu hỏi nằm trên một dòng.
/// </para>
///
/// <para>
/// Dòng KHÔNG có thẻ đi qua nguyên vẹn thành mục không nhóm (<c>Group</c> rỗng) — đúng thứ mà
/// <c>InterviewOutlookService.Canonicalize</c> tạo ra khi model viết một nhãn không khớp nhóm nào.
/// </para>
/// </summary>
public static class OpenQuestionFixture
{
    /// <summary>Danh sách điểm tồn đọng dựng từ các dòng <c>[Nhóm] câu hỏi</c>.</summary>
    public static IReadOnlyList<OpenQuestionEntry> Items(params string[] lines)
        => lines.Select(Item).ToList();

    /// <summary>Một điểm tồn đọng dựng từ dòng <c>[Nhóm] câu hỏi</c>; không có thẻ ⇒ mục không nhóm.</summary>
    public static OpenQuestionEntry Item(string line)
    {
        var match = TaggedLine.Match((line ?? string.Empty).Trim());
        return match.Success
            ? new OpenQuestionEntry { Group = match.Groups["group"].Value.Trim(), Text = match.Groups["text"].Value.Trim() }
            : new OpenQuestionEntry { Text = (line ?? string.Empty).Trim() };
    }

    /// <summary>Một mục ĐÃ TRẢ LỜI dựng từ dòng <c>[Nhóm] câu hỏi</c> + câu trả lời đã thu được.</summary>
    public static OpenQuestionEntry Answered(string line, string answer)
    {
        var item = Item(line);
        item.Status = OpenQuestionEntry.Answered;
        item.Answer = answer;
        return item;
    }

    /// <summary>Chuỗi JSON đúng như thứ được lưu trong <c>Project.OpenQuestions</c>.</summary>
    public static string? Stored(params string[] lines)
        => InterviewOutlookParser.SerializeOpenQuestions(Items(lines));

    private static readonly Regex TaggedLine = new(@"^\[(?<group>[^\]]{1,80})\]\s*(?<text>.+)$", RegexOptions.Singleline);
}
