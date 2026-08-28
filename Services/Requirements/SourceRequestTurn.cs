namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Lượt XIN FILE tất định: người dùng vừa nhắc tới một nơi dữ liệu đang nằm sẵn (*"bên em có file excel
/// danh sách JD"*) mà dự án chưa có tài liệu nguồn nào ⇒ lượt kế tiếp chỉ làm đúng một việc — xin cái
/// file đó.
///
/// <para>
/// <b>Vì sao phải là code chứ không phải một dòng prompt nữa.</b> <c>requirement-chat.v4.md</c> đã ghi luật
/// này ở mục "Lượt mở đầu" bằng chữ in đậm — *"Người dùng nhắc tới một nguồn dữ liệu họ đang dùng thì xin
/// file NGAY TẠI LƯỢT ĐÓ"* — kèm cả một ca thật. Nó vẫn trượt, và trượt im lặng: không cổng nào biết rằng
/// buổi phỏng vấn vừa bỏ qua một file. Ca thật (dự án JD Libary 5, lượt 3 và 5): người dùng kể *"có 1 file
/// excel danh sách JD được dùng trong nhà máy… và 1 file excel khác để quản lý JD được gán cho nhân
/// viên"*, nhắc TỚI HAI LẦN, và BA không xin lần nào trong suốt 26 lượt. Hậu quả không dừng ở việc thiếu
/// một tệp đính kèm: không có file thì không có BẢNG CỘT để người dùng chốt phạm vi cột
/// (<see cref="SourceColumnMapBuilder"/>), nên toàn bộ mô hình dữ liệu của dự án được dựng từ trí nhớ của
/// người dùng gõ tay trong một lượt chat — đúng thứ đang nằm sẵn trong file.
/// </para>
///
/// <para>
/// <b>Lượt này ĐỨNG MỘT MÌNH, và đó là lý do nó không có dấu hỏi.</b> Xin file là một lời nhờ HÀNH ĐỘNG:
/// người dùng đọc xong sẽ đi tìm file, nên mọi câu hỏi kèm trong lượt đó bị nuốt mất — nhưng bản đồ bao
/// phủ vẫn tính là đã hỏi. Vì vậy nó thay trọn lượt: không chip, không thẻ hỏi, ô nhập nhận vai chỗ trả
/// lời (đính kèm, hoặc nhắn một tiếng là không có file).
/// </para>
///
/// <para>
/// <b>Chỉ bắn MỘT lần.</b> Điều kiện gồm cả "chưa có tài liệu nguồn nào" lẫn "chưa lượt BA nào xin file"
/// (<see cref="Looks"/> dò trên các lượt đã lưu). Người dùng nói không có file thì hội thoại đi tiếp bình
/// thường và lượt này không bao giờ quay lại — giục lần hai là phí đúng cái lượt mà luật này sinh ra để
/// tiết kiệm.
/// </para>
/// </summary>
public static class SourceRequestTurn
{
    /// <summary>
    /// Lượt xin file dựng sẵn. Nêu rõ ĐƯỜNG gửi (nút 📎 dưới ô nhập) vì người dùng nghiệp vụ không tự tìm,
    /// và mở sẵn đường thoát ("chưa tiện gửi thì nhắn một tiếng") để lượt này không thành ngõ cụt cho
    /// người không có file trong tay.
    /// </summary>
    public const string Message =
        "Trước khi hỏi tiếp, anh/chị gửi giúp mình file đang dùng nhé — bấm nút 📎 ngay dưới ô nhập, "
        + "hoặc kéo-thả file vào khung chat. Đọc được file thì mình đỡ phải hỏi lại những gì trong đó đã có "
        + "sẵn. Chưa tiện gửi thì anh/chị nhắn cho mình một tiếng, mình hỏi tiếp bằng câu hỏi.";

    /// <summary>
    /// Lượt user này có nhắc tới một nguồn dữ liệu ĐANG DÙNG không. Danh sách cụm cố ý HẸP và chỉ gồm các
    /// vật mang dữ liệu có thể đính kèm được — cùng tinh thần với <c>NarrativeCues</c> của
    /// <see cref="BAChatReplyParser"/>: lọt lưới thì mất một cơ hội xin file (bằng đúng hành vi hôm nay),
    /// còn bắt quá tay thì đốt một lượt của người dùng.
    /// </summary>
    public static bool MentionsExistingSource(string? userMessage)
    {
        var value = (userMessage ?? string.Empty).ToLowerInvariant();
        return value.Length > 0 && SourceCues.Any(cue => value.Contains(cue, StringComparison.Ordinal));
    }

    private static readonly string[] SourceCues =
    {
        "excel", "bảng tính", "spreadsheet", "google sheet", "sheet", "file", "biểu mẫu", "mẫu giấy", "form giấy"
    };

    /// <summary>
    /// Lượt BA này có phải một lời xin file không — dùng để (1) không xin lần thứ hai, và (2) miễn cho nó
    /// chốt chặn LƯỢT CÂM ở <see cref="BAChatService"/>: nó cố ý không có dấu hỏi, nhưng ô nhập vẫn là chỗ
    /// trả lời thật (đính kèm hoặc nhắn lại).
    /// </summary>
    public static bool Looks(string? message)
    {
        var value = (message ?? string.Empty).ToLowerInvariant();
        if (value.Length == 0)
            return false;

        return value.Contains("📎", StringComparison.Ordinal)
            || value.Contains("đính kèm", StringComparison.Ordinal)
            || (value.Contains("file", StringComparison.Ordinal)
                && (value.Contains("gửi giúp", StringComparison.Ordinal)
                    || value.Contains("gửi cho mình", StringComparison.Ordinal)
                    || value.Contains("kéo-thả", StringComparison.Ordinal)));
    }
}
