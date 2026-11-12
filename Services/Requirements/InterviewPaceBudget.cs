using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// NGÂN SÁCH LƯỢT của buổi phỏng vấn: sau bao nhiêu lượt người dùng đã trả lời thì BA phải thôi đào thêm
/// và chuyển sang ĐÓNG các nhóm còn hở (khối "## CHẶNG VỀ ĐÍCH" của <see cref="BAChatPromptBlocks"/>).
///
/// <para>
/// <b>Vì sao cần một con số ở code.</b> Prompt đã dạy BA gộp câu hỏi và đi hết từng chủ đề, nhưng không
/// luật nào trong đó biết buổi phỏng vấn ĐÃ DÀI BAO LÂU — model không đếm được lượt, và bản đồ bao phủ chỉ
/// nói "còn thiếu gì", không nói "đã tốn bao nhiêu". Ca thật (dự án <i>Locker management 7</i>): 46 lượt
/// người dùng trong hơn 70 phút, 7/12 nhóm vẫn chưa <c>[RÕ]</c> và chưa bảng chốt nào được bày — trong khi
/// người dùng muốn cả buổi gói trong một giờ. Không có gì báo cho BA rằng nó đã tiêu hết phần thời gian
/// dành cho hỏi đáp.
/// </para>
///
/// <para>
/// <b>Con số đến từ đâu.</b> Đo trên chính buổi đó, một vòng hỏi–đáp tốn của người dùng ~95 giây (~30
/// giây chờ BA + ~65 giây đọc và trả lời). Năm bảng chốt cuối buổi cần 15–20 phút, nên phần hỏi đáp của
/// một buổi một giờ còn chỗ cho khoảng 40 phút ≈ 25 vòng.
/// </para>
///
/// <para>
/// <b>Đây KHÔNG phải cổng "chốt nhanh phần còn lại" đã bỏ.</b> Cổng cũ để BA tự soạn phương án cho mọi
/// nhóm còn trống rồi xin MỘT cái gật, và phương án ấy vào hội thoại như lời người dùng. Ở đây không có
/// lượt nào do máy viết thay người dùng và không có cái gật chung nào: BA vẫn là bên HỎI, mỗi điểm còn
/// hụt là một câu đóng riêng trong thẻ hỏi gộp (trần 4 câu vẫn áp), người dùng chốt TỪNG câu. Thứ đổi
/// duy nhất là BA thôi mở chủ đề mới. Cổng "Write Requirement" không đổi: nó vẫn chỉ mở khi bản đồ đủ.
/// </para>
/// </summary>
public static class InterviewPaceBudget
{
    /// <summary>
    /// Số lượt NGƯỜI DÙNG đã trả lời mà từ đó BA vào chặng về đích. Prompt chat nhắc cùng con số này ở mục
    /// "Ngân sách buổi phỏng vấn" — <c>InterviewPaceRuleTests</c> giữ hai bên khớp nhau.
    /// </summary>
    public const int ClosingAfterUserTurns = 25;

    /// <summary>Buổi phỏng vấn đã tiêu hết ngân sách hỏi đáp chưa.</summary>
    public static bool IsClosing(int userTurnCount) => userTurnCount >= ClosingAfterUserTurns;

    /// <summary>
    /// Các nhóm còn hở mà BA phải đóng BẰNG CÂU HỎI ở chặng về đích: mọi dòng <c>[MỘT PHẦN]</c>/
    /// <c>[CHƯA HỎI]</c>, trừ những nhóm đang chờ một BẢNG chốt (<paramref name="tableDeferredLabels"/>) —
    /// BA bị cấm hỏi lẻ các nhóm đó, nêu tên chúng ở đây là hai mệnh lệnh chọi nhau trong cùng một ngữ
    /// cảnh. Trả rỗng khi không còn nhóm nào như vậy: lúc đó việc còn lại là của các cổng bảng, khối
    /// chặng về đích không có gì để nói.
    /// </summary>
    public static List<string> GroupsToClose(string? coverageMap, IReadOnlyCollection<string> tableDeferredLabels)
        => CoverageMapParser.Parse(coverageMap)
            .Where(x => x.Status is CoverageStatus.Partial or CoverageStatus.NotAsked)
            .Select(x => (x.Label ?? string.Empty).Trim())
            .Where(label => label.Length > 0
                && !tableDeferredLabels.Any(t => label.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
}
