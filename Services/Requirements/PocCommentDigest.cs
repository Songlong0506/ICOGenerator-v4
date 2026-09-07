using System.Text;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Cách DUY NHẤT để rải các ghi chú người dùng ghim trên bản demo POC vào một prompt.
/// <para>
/// Bốn đường khác nhau cùng đưa tập ghi chú này cho LLM — phân loại (<c>TriagePocFeedbackUseCase</c>),
/// chuyển thành lượt chat (<c>RoutePocFeedbackToRequirementUseCase</c>), rút bài học vào trí nhớ
/// (<c>PocFeedbackMemoryService</c>) và chốt quy ước trình bày (<c>PocUiConventionService</c>) — và cả
/// bốn từng chép cùng một đoạn dựng dòng. Định dạng dòng là HỢP ĐỒNG với bốn prompt tương ứng, nên một
/// bản sao bị sửa lệch sẽ làm đúng một trong bốn prompt đọc sai ngữ cảnh mà không có lỗi nào nổi lên.
/// </para>
/// </summary>
public static class PocCommentDigest
{
    /// <summary>Danh sách gạch đầu dòng — dùng khi prompt chỉ cần NỘI DUNG các ghi chú.</summary>
    public static void AppendBullets(StringBuilder sb, IEnumerable<PocComment> comments)
    {
        foreach (var comment in comments)
        {
            sb.Append("- ");
            AppendBody(sb, comment);
        }
    }

    /// <summary>
    /// Danh sách ĐÁNH SỐ từ 1. Số thứ tự là hợp đồng duy nhất giữa prompt và code ở đường phân loại:
    /// model trả về index, phía gọi map ngược về <c>PocComment.Id</c> theo ĐÚNG thứ tự này.
    /// </summary>
    public static void AppendNumbered(StringBuilder sb, IReadOnlyList<PocComment> comments)
    {
        for (var i = 0; i < comments.Count; i++)
        {
            sb.Append(i + 1).Append(". ");
            AppendBody(sb, comments[i]);
        }
    }

    // Phần sau dấu đầu dòng: vị trí (màn hình / phần tử) rồi tới nội dung. Hai vế vị trí đều tùy chọn.
    private static void AppendBody(StringBuilder sb, PocComment comment)
    {
        if (!string.IsNullOrWhiteSpace(comment.PageView))
            sb.Append($"[Màn hình \"{comment.PageView}\"] ");
        if (!string.IsNullOrWhiteSpace(comment.ElementLabel))
            sb.Append($"Phần tử: {comment.ElementLabel} — ");
        sb.AppendLine(comment.Comment.Trim());
    }
}
