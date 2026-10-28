using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Các khối lệnh dựng từ DỮ LIỆU cho lượt BA đọc tài liệu nguồn vừa upload
/// (<see cref="BAChatService.AcknowledgeSourcesAsync"/>). Phần luật viết bản đọc hiểu — giọng, cấu trúc, cách
/// xử điều chưa chắc — nằm ở prompt <c>source-ack.v3.md</c>, đo được ở Prompt Evals và sửa được ở Prompt
/// Studio; ở đây chỉ có những điều prompt KHÔNG có cách nào tự biết: file nào cần chốt bảng cột, file nào
/// thật sự vừa được gửi, và người dùng đã kể gì tới lúc này.
/// </summary>
public static class BASourceAckPrompt
{
    /// <summary>Trần ký tự của phần lượt hội thoại nguyên văn trong <see cref="ConversationContext"/>.</summary>
    public const int MaxConversationChars = 12_000;

    /// <summary>Số lượt gần nhất tối đa được kể lại nguyên văn trong <see cref="ConversationContext"/>.</summary>
    public const int MaxConversationTurns = 30;

    /// <summary>
    /// Khối gọi tên các file BẢNG TÍNH mà lượt này phải điền <c>columns</c>. Model nhìn thấy text của MỌI
    /// nguồn trong project (kể cả file đã có bảng cột từ lần upload trước) nên nó không tự biết file nào còn
    /// chờ — thiếu dòng này nó hoặc dựng lại bảng cho file cũ, hoặc bỏ qua file mới.
    ///
    /// <para>
    /// Bảng cột không còn là thứ người dùng phải rà rồi gửi: <see cref="BAChatService"/> lưu luôn đề xuất của
    /// BA vào <see cref="ProjectSourceFile.ColumnMap"/>, và người dùng chỉ mở ra sửa khi muốn. Lượt cũ bắt họ
    /// tích bảng trước, rồi mới kể lại file ở lượt sau kèm cụm "Chỗ chưa chắc" — ba lượt trôi qua trước khi BA
    /// nói được điều gì người dùng chưa biết.
    /// </para>
    /// </summary>
    public static string ColumnScope(IReadOnlyList<string> pendingColumnFiles)
        => pendingColumnFiles.Count > 0
            ? "## BẢNG CỘT CỦA LƯỢT NÀY\n"
              + "File bảng tính cần chốt phạm vi cột: " + string.Join(", ", pendingColumnFiles) + ".\n"
              + "Điền `columns` phủ đủ mọi cột của các file đó (ý nghĩa viết sẵn, `used` theo nghiệp vụ). Hệ thống "
              + "LƯU LUÔN bảng này — người dùng không phải tích hay gửi gì — nên trong `message` chỉ cần ĐÚNG MỘT "
              + "câu nói ứng dụng dựng trên cột nào, bỏ qua cột nào của hệ cũ."
            : "## BẢNG CỘT CỦA LƯỢT NÀY\n"
              + "Không có file bảng tính nào cần chốt phạm vi cột ⇒ `columns` là mảng RỖNG, và `message` không "
              + "có câu phạm vi cột.";

    /// <summary>
    /// PHẠM VI KỂ LẠI của lượt đọc file: gọi đích danh các file VỪA GỬI, và nói thẳng rằng các nguồn cũ chỉ
    /// đính kèm để đối chiếu.
    ///
    /// <para>
    /// Ca thật đã gặp: người dùng gửi một file Excel từ đầu buổi, nhiều lượt sau gửi thêm một ảnh chụp biểu
    /// mẫu để trả lời một câu hỏi — và BA kể lại CẢ HAI, mở đầu lượt bằng gần nửa số dòng nói lại đúng bộ cột
    /// của file Excel. Model không sai luật nào nó được cho: nó thấy text của mọi nguồn nằm dưới cùng một câu
    /// "tôi vừa đính kèm". Chỗ hỏng là cơ chế nói dối về chữ "vừa gửi", nên chỗ vá cũng phải ở đây — prompt
    /// không có cách nào tự biết file nào mới.
    /// </para>
    ///
    /// <para>
    /// Nguồn cũ vẫn ĐI KÈM, và ngoại lệ cho phép nhắc tên chúng là cố ý: điểm đắt nhất của một lô upload
    /// thường nằm đúng ở chỗ NỐI giữa file mới và file cũ (biểu mẫu vừa gửi lấy người học từ file danh sách
    /// hay tự nhập?). Cấm nhắc tên là cắt luôn điều đó.
    /// </para>
    /// </summary>
    public static string ReadbackScope(IReadOnlyList<string> justSentFiles, IReadOnlyList<string> earlierFiles)
    {
        if (justSentFiles.Count == 0)
            return string.Empty;

        var scope = "## PHẠM VI KỂ LẠI CỦA LƯỢT NÀY\n"
            + "File người dùng VỪA GỬI ở lượt này: " + string.Join(", ", justSentFiles) + ". Chỉ những file "
            + "này mới là thứ lượt này phải đọc hiểu và kể lại.\n";

        if (earlierFiles.Count == 0)
            return scope;

        return scope
            + "Các nguồn còn lại — " + string.Join(", ", earlierFiles) + " — đã gửi và đã được đọc từ TRƯỚC; "
            + "chúng đính kèm ở đây CHỈ để bạn đối chiếu. TUYỆT ĐỐI KHÔNG kể lại chúng: không mô tả lại nội "
            + "dung/cột/quy mô của chúng. Kể lại một file đã đọc là bắt người dùng nghe lại đúng thứ họ đã nghe, "
            + "trong khi file họ thật sự vừa gửi bị đẩy xuống nửa dưới của lượt.\n"
            + "Được phép nhắc tên chúng đúng MỘT trường hợp: một phát hiện hoặc câu hỏi nằm ở chỗ NỐI giữa file "
            + "vừa gửi và chúng (dữ liệu bên này lấy từ bên kia hay nhập tay?). Đó là điều chỉ lộ ra khi đặt "
            + "hai nguồn cạnh nhau, và nó thuộc về file vừa gửi.";
    }

    /// <summary>
    /// Khối <c>## BỐI CẢNH HỘI THOẠI</c>: những gì người dùng đã kể tới lúc này — tóm tắt dài hạn, bản đồ bao
    /// phủ, và các lượt gần nhất nguyên văn. Trả rỗng khi chưa có gì để kể (upload ngay lượt đầu).
    ///
    /// <para>
    /// Trước đây lượt đọc file chỉ nhận prompt + text nguồn, KHÔNG có một dòng hội thoại nào — trong khi chính
    /// prompt bắt nó "đối chiếu với điều người dùng đã kể". Kết quả là bản đọc chỉ kể lại số đếm của file như
    /// một vật thể độc lập; còn phần đắt nhất — file là BẰNG CHỨNG cho đúng nỗi đau người dùng vừa nói (Status
    /// để trống ↔ "data không được update", mã nhân viên trống ↔ "nghỉ việc phải dò tay") — không có cách nào
    /// viết ra, vì model không biết họ đã nói gì. Câu hỏi kết của lượt cũng cần khối này để không hỏi lại điều
    /// đã được trả lời.
    /// </para>
    ///
    /// <para>
    /// Chỉ đọc những gì ĐÃ có (tóm tắt + bản đồ đã lưu trên project), không gọi LLM gộp tóm tắt như lượt chat
    /// thường: lượt đọc file đã là lời gọi nặng nhất của buổi (text nguồn + ảnh), thêm một lời gọi phụ trước
    /// nó chỉ để lấy ngữ cảnh là kéo dài đúng lúc người dùng đang chờ. Phần lượt nguyên văn có trần riêng
    /// (<see cref="MaxConversationChars"/>) và giữ ĐUÔI — lượt gần nhất thường là lượt BA vừa xin file, tức
    /// chính lý do file này tới.
    /// </para>
    /// </summary>
    public static string ConversationContext(string? summary, string? coverageMap, IEnumerable<AgentConversation> turns)
    {
        var lines = turns
            .Where(t => !string.IsNullOrWhiteSpace(t.Message)
                        && !t.Message.StartsWith(RequirementScreenText.LlmFailurePrefix, StringComparison.Ordinal))
            .TakeLast(MaxConversationTurns)
            .Select(ConversationTurnRenderer.Render)
            .ToList();

        // Giữ ĐUÔI trong trần ký tự: bỏ dần lượt cũ nhất, không cắt giữa một lượt.
        var kept = new List<string>();
        var used = 0;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (used + lines[i].Length > MaxConversationChars && kept.Count > 0)
                break;
            kept.Insert(0, lines[i]);
            used += lines[i].Length;
        }

        if (string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(coverageMap) && kept.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("## BỐI CẢNH HỘI THOẠI");
        sb.AppendLine("Những gì người dùng đã kể tới lúc này — dùng để đặt tài liệu cạnh lời kể và để câu hỏi kết "
            + "không hỏi lại điều đã có câu trả lời. Đây là ngữ cảnh, không phải tài liệu cần kể lại.");
        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine();
            sb.AppendLine("### Tóm tắt các lượt cũ");
            sb.AppendLine(summary.Trim());
        }
        if (!string.IsNullOrWhiteSpace(coverageMap))
        {
            sb.AppendLine();
            sb.AppendLine("### Bản đồ bao phủ hiện tại");
            sb.AppendLine(coverageMap.Trim());
        }
        if (kept.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Các lượt gần nhất");
            foreach (var line in kept)
                sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }
}
