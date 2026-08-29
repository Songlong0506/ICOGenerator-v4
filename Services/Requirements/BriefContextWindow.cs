namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Quy tắc TẤT ĐỊNH trả lời đúng một câu: lượt soạn/soát/sửa Product Brief được phép bỏ bao nhiêu lượt
/// hội thoại CŨ ra khỏi phần gửi nguyên văn.
/// <para>
/// Vì sao cần: transcript từng là input DUY NHẤT của bước soạn Brief không có trần — mọi khối khác đã bị
/// chặn trên (bản đồ bao phủ 4000 ký tự, nhật ký/tồn đọng 4000, tóm tắt hội thoại 6000, text tài liệu
/// nguồn theo <c>Llm:SourceUpload:MaxTextCharsPerFile</c>). Một buổi phỏng vấn dài vì thế đẩy nguyên
/// hội thoại lên model BA LẦN trong một lượt bấm (soạn → tự soát → sửa), và khi vượt context window thì
/// không có degrade mềm: lời gọi hỏng ⇒ <see cref="ProductBriefDraftService"/> ném ⇒ task fail.
/// </para>
/// <para>
/// BẤT BIẾN (chỗ dễ làm hỏng nhất nếu sửa sau này): <b>chỉ được bỏ lượt đã nằm trong
/// <c>Project.ConversationSummary</c></b> — tức không bao giờ cắt quá <c>SummarizedTurnCount</c>. Cắt
/// xa hơn là làm bốc hơi thông tin: phần bị bỏ không nằm trong tóm tắt, không nằm trong transcript, và
/// vòng tự soát (đối chiếu bản nháp với hội thoại) sẽ không thấy được thứ nó phải đối chiếu.
/// </para>
/// Ba nguồn "mong muốn cắt", lấy cái cắt nhiều nhất rồi kẹp lại bằng bất biến trên:
/// <list type="number">
/// <item>Trần số lượt (<see cref="MaxVerbatimTurns"/>).</item>
/// <item>Trần ký tự (<see cref="MaxVerbatimChars"/>) — lượt hội thoại rất lệch nhau về độ dài (một lượt
/// chốt bảng phân quyền dài bằng vài chục lượt hỏi đáp), nên đếm lượt một mình không chặn được token.</item>
/// <item>Mốc duyệt Brief (<c>Project.BriefApprovedTurnCount</c>) — phần trước mốc đã được Product Brief
/// ĐÃ DUYỆT chở, và đó là bản duy nhất người dùng đã ký.</item>
/// </list>
/// </summary>
public static class BriefContextWindow
{
    /// <summary>
    /// Số lượt gần nhất luôn gửi nguyên văn. Bằng đúng
    /// <see cref="ConversationMemoryService.RecentWindowTurns"/> (40) — trước đây rộng gấp đôi cửa sổ
    /// chat (20) vì dẫn câu hỏi kế tiếp chỉ cần vài lượt gần đây còn VIẾT tài liệu thì cần đủ chi tiết
    /// để không phải đoán; nay cửa sổ chat đã nới lên bằng nó nên khoảng cách đó không còn.
    /// Hệ quả cần biết: con trỏ tóm tắt trôi chậm hơn trước, mà bất biến bên dưới cấm cắt quá con trỏ,
    /// nên transcript soạn Brief giữ nguyên văn nhiều lượt hơn trước (~40 thay vì ~20-29). Đây là đánh
    /// đổi có chủ ý cho model context lớn; hạ <see cref="ConversationMemoryService.RecentWindowTurns"/>
    /// nếu quay lại dùng model context nhỏ.
    /// </summary>
    public const int MaxVerbatimTurns = 40;

    /// <summary>Trần ký tự của phần nguyên văn (đo trên text đã render của từng lượt).</summary>
    public const int MaxVerbatimChars = 40_000;

    /// <summary>
    /// Số lượt CŨ NHẤT được bỏ khỏi transcript nguyên văn.
    /// </summary>
    /// <param name="turnLengths">Độ dài text đã render của TỪNG lượt, theo đúng thứ tự thời gian và
    /// KHÔNG bỏ lượt nào — các con trỏ bộ nhớ đếm trên toàn bộ số dòng hội thoại, nên lượt bị lọc khỏi
    /// transcript (lượt rỗng, lượt báo lỗi gọi AI) vẫn phải có mặt ở đây với độ dài 0.</param>
    /// <param name="summarizedTurnCount">Con trỏ tóm tắt (<c>Project.SummarizedTurnCount</c>).</param>
    /// <param name="approvedTurnCount">Mốc duyệt Brief (<c>Project.BriefApprovedTurnCount</c>).</param>
    public static int ComputeSkip(IReadOnlyList<int> turnLengths, int summarizedTurnCount, int approvedTurnCount)
    {
        var total = turnLengths.Count;
        if (total == 0)
            return 0;

        var desired = Math.Max(
            Math.Max(total - MaxVerbatimTurns, SkipToFitChars(turnLengths)),
            approvedTurnCount);

        // Bất biến: không cắt quá phần đã được tóm tắt. Con trỏ có thể lớn hơn số lượt hiện có (dữ liệu
        // cũ, lượt bị xóa) nên kẹp cả hai đầu — và luôn chừa lại ÍT NHẤT một lượt nguyên văn: mốc duyệt
        // có thể trùng đúng lượt cuối (duyệt xong chưa nói thêm gì), mà một transcript rỗng thì vòng tự
        // soát không còn gì để đối chiếu bản nháp.
        return Math.Clamp(Math.Min(desired, summarizedTurnCount), 0, Math.Max(0, total - 1));
    }

    // Bỏ dần từ lượt cũ nhất cho tới khi phần còn lại lọt trần ký tự. Một lượt đơn lẻ dài hơn cả trần thì
    // vòng lặp dừng ở chính nó (bỏ hết những lượt trước) chứ không bỏ sạch hội thoại.
    private static int SkipToFitChars(IReadOnlyList<int> turnLengths)
    {
        long kept = 0;
        foreach (var length in turnLengths)
            kept += length;

        var skip = 0;
        while (skip < turnLengths.Count - 1 && kept > MaxVerbatimChars)
        {
            kept -= turnLengths[skip];
            skip++;
        }

        return skip;
    }
}
