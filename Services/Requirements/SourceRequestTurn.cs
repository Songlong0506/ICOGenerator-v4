using ICOGenerator.Domain;
using ICOGenerator.Localization;
namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Lượt XIN FILE dựng sẵn: người dùng vừa nhắc tới một nơi dữ liệu đang nằm sẵn (*"bên em có file excel
/// danh sách JD"*) mà dự án chưa có tài liệu nguồn nào ⇒ lượt kế tiếp chỉ làm đúng một việc — xin cái
/// file đó.
///
/// <para>
/// <b>Vì sao phải là code chứ không phải một dòng prompt nữa.</b> <c>requirement-chat.v4.md</c> đã ghi luật
/// này ở mục "Xin file NGAY TẠI LƯỢT ĐÓ" bằng chữ in đậm — *"Người dùng nhắc tới một nguồn dữ liệu họ đang dùng thì xin
/// file NGAY TẠI LƯỢT ĐÓ"* — kèm cả một ca thật. Nó vẫn trượt, và trượt im lặng: không cổng nào biết rằng
/// buổi phỏng vấn vừa bỏ qua một file. Ca thật (dự án JD Libary 5, lượt 3 và 5): người dùng kể *"có 1 file
/// excel danh sách JD được dùng trong nhà máy… và 1 file excel khác để quản lý JD được gán cho nhân
/// viên"*, nhắc TỚI HAI LẦN, và BA không xin lần nào trong suốt 26 lượt. Hậu quả không dừng ở việc thiếu
/// một tệp đính kèm: không có file thì không có BẢNG CỘT chốt phạm vi cột
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
/// <b>Chỉ bắn MỘT lần — cho một lời xin ĐÍCH DANH.</b> Điều kiện gồm cả "chưa có tài liệu nguồn nào" lẫn
/// "chưa lượt BA nào xin đúng cái nguồn người dùng đã nhắc" (<see cref="AnsweredMention"/>). Người dùng nói
/// không có file thì hội thoại đi tiếp bình thường và lượt này không bao giờ quay lại — giục lần hai là phí
/// đúng cái lượt mà luật này sinh ra để tiết kiệm. Người dùng lờ lời xin đi (đáp chữ, không đính kèm) thì
/// BA được nhắc lại MỘT lần bằng lời của chính nó, theo luật chung về câu chưa được trả lời của phanh chống
/// hỏi lại — không phải bằng đường tất định này.
/// </para>
///
/// <para>
/// <b>Lời mời chung chung không đốt suất.</b> Prompt bắt lượt mở đầu mời người dùng kể một mạch VÀ đính kèm
/// tài liệu nếu có — nên lượt mở đầu nào cũng có nút 📎 và mang cờ <c>SourceRequested</c>. Từng đếm cờ đó là
/// đủ, và đường này vì thế gần như không bao giờ bắn: ca thật (dự án Learning &amp; Development) người dùng
/// đáp *"File Excel, Email"* ngay lượt sau lời mời ấy, BA tự viết lời xin đúng file Excel đó, lời xin bị
/// phanh chống hỏi lại thay đi, và đường tất định cũng im vì "đã xin rồi". Lời mời trước khi người dùng nói
/// có file là một lời mời; lời xin sau khi họ nói có mới là lời xin.
/// </para>
/// </summary>
public static class SourceRequestTurn
{
    /// <summary>
    /// Đã có lượt BA nào xin file NGAY SAU một lượt người dùng nhắc tới nguồn dữ liệu đang có chưa — tức
    /// suất xin file đích danh đã dùng chưa. Cặp được xét trên các lượt THUỘC buổi làm việc: lượt ⚠️ và cặp
    /// hỏi–đáp ngoài phạm vi không tách được một lượt nhắc nguồn khỏi lượt BA đáp nó, vì lượt BA thật sự đáp
    /// là lượt kế tiếp còn lại.
    /// </summary>
    public static bool AnsweredMention(IEnumerable<AgentConversation> turns)
    {
        var afterMention = false;
        foreach (var turn in turns)
        {
            if (ConversationTurnRenderer.CarriesNoRequirement(turn))
                continue;

            if (!ConversationTurnRenderer.IsAssistant(turn))
            {
                afterMention = turn.MentionsSource;
                continue;
            }

            if (afterMention && turn.SourceRequested)
                return true;
            afterMention = false;
        }

        return false;
    }

    /// <summary>
    /// Lượt xin file dựng sẵn. Nêu rõ ĐƯỜNG gửi (nút 📎 dưới ô nhập) vì người dùng nghiệp vụ không tự tìm,
    /// và mở sẵn đường thoát ("chưa tiện gửi thì nhắn một tiếng") để lượt này không thành ngõ cụt cho
    /// người không có file trong tay.
    /// </summary>
    public static string Message => Loc.T("SourceRequestTurn.Message");

    /// <summary>
    /// Lượt BA này có phải lời xin file DO CƠ CHẾ dựng (<see cref="Message"/>) không — nhận ra bằng chính
    /// dấu hiệu máy của nó, nút 📎, thứ không phụ thuộc ngôn ngữ của hội thoại.
    ///
    /// <para>
    /// ĐỪNG TÌM <c>MentionsExistingSource</c> và bảng cụm "excel / bảng tính / file / biểu mẫu…": "lượt user
    /// có nhắc tới một nguồn dữ liệu đang dùng không" và "lượt model tự viết này có phải lời xin file
    /// không" là hai câu hỏi về NGHĨA, nay do <see cref="InterviewJudge"/> phán
    /// (<c>userMentionsExistingSource</c>, <c>asksForFile</c>). Bảng cụm cũ vừa bắt quá tay (chữ "file"
    /// trong <i>"cho phép upload file đính kèm"</i> — một tính năng, không phải một nguồn dữ liệu), vừa
    /// phải kê lại cho từng ngôn ngữ. Chỗ đọc gộp cả hai nguồn là <c>BAChatTurnDraft.AsksForFile</c>.
    /// </para>
    /// </summary>
    public static bool Looks(string? message)
        => !string.IsNullOrEmpty(message) && message.Contains("📎", StringComparison.Ordinal);
}
