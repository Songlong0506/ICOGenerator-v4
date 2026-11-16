namespace ICOGenerator.Domain;

/// <summary>
/// Điểm dừng BỀN của một lượt agent nhiều bước, gắn với đúng một <see cref="AgentTask"/>: toàn bộ hội
/// thoại agent ↔ model ↔ tool tính tới bước gần nhất đã xong.
///
/// <para>
/// Vì sao cần: vòng lặp agent giữ hội thoại trong RAM, nên một lời gọi model hỏng ở bước 10 (chạm hạn
/// mức, rớt kết nối) từng xoá sạch 9 bước trước — "Thử lại" phải dựng lại từ đầu và trả lại toàn bộ số
/// token đó. Sản phẩm của các bước ấy vẫn nằm trên đĩa (mỗi tool ghi thẳng ra workspace); thứ duy nhất
/// mất là hội thoại, và đây là chỗ giữ nó.
/// </para>
///
/// <para>
/// Một task có nhiều nhất MỘT dòng, được ghi đè sau mỗi bước (xem
/// <c>Services.Agents.CheckpointingChatClient</c>) và xoá khi task hoàn tất. Task CHỈNH SỬA là một
/// <see cref="AgentTask"/> mới nên không bao giờ thừa hưởng checkpoint của vòng trước.
/// </para>
/// </summary>
public class AgentRunCheckpoint
{
    /// <summary>Khoá chính, đồng thời là khoá ngoại tới <see cref="AgentTask"/> (xoá task là xoá checkpoint).</summary>
    public Guid TaskId { get; set; }
    public AgentTask Task { get; set; } = default!;

    /// <summary>
    /// Danh sách <c>ChatMessage</c> (JSON) mà lời gọi model kế tiếp phải gửi đi. Chứa lại nguyên văn spec,
    /// tham số tool và kết quả tool nên mã hoá at rest như <c>AgentModelCallLog.RequestJson</c>.
    /// </summary>
    public string MessagesJson { get; set; } = string.Empty;

    /// <summary>
    /// Số lời gọi model đã THÀNH CÔNG. 0 = lượt chạy chết ngay ở lời gọi đầu, chưa có gì đáng giữ — khi đó
    /// lần thử lại chạy như một lượt mới chứ không "nối tiếp" một hội thoại rỗng.
    /// </summary>
    public int CompletedSteps { get; set; }

    /// <summary>
    /// <c>ModelId</c> đã chạy các bước trên. Lần chạy tiếp dùng model khác thì phần suy luận ẩn (chữ ký gắn
    /// với đúng model sinh ra nó) phải bị gỡ khỏi hội thoại trước khi gửi.
    /// </summary>
    public string? ModelId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
