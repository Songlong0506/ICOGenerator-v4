using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Nhận biết những thứ một endpoint OpenAI-<i>compatible</i> TỪ CHỐI, và sửa lời gọi để thử lại. Đây là
/// mặt sau của <see cref="OpenAiCompatibility"/> (vốn lo hình dạng request ĐI RA): ở đây là "endpoint vừa
/// trả 400 vì cái gì, và bỏ bớt gì thì lượt này còn cứu được".
///
/// Tách khỏi <see cref="LlmClient"/> để nó chỉ còn phần điều phối, và để thêm một quirk mới sau này
/// (endpoint mới, tham số mới bị từ chối) là sửa đúng file này.
///
/// Cả hai quirk đều bắt bằng cách khớp TÊN THAM SỐ trong body lỗi, nên không cần bảng theo từng nhà cung cấp.
/// </summary>
internal static class EndpointQuirks
{
    /// <summary>
    /// Endpoint chỉ nhận text từ chối phần ảnh bằng 400 có nêu tên loại content — DeepSeek:
    /// <c>"unknown variant `image_url`, expected `text`"</c>. Xảy ra khi model bị tick nhầm
    /// <c>SupportsVision</c> ở trang Models.
    /// </summary>
    public static bool RejectedImageContent(LlmCallResult result) =>
        !result.IsSuccess && result.ResponseText.Contains("image_url", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Endpoint không cài đặt mức <c>response_format</c> được xin sẽ trả 400 nêu đúng tên tham số —
    /// DeepSeek: <c>"This response_format type is unavailable now"</c>; server tự host thường là
    /// <c>"response_format is not supported"</c>.
    /// </summary>
    public static bool RejectedResponseFormat(LlmCallResult result) =>
        !result.IsSuccess && result.ResponseText.Contains("response_format", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lời gọi chết TRƯỚC khi có bất kỳ HTTP response nào (DNS/TCP/TLS/proxy, hoặc phía kia đóng kết nối
    /// giữa lúc app đang ghi body). Với một lượt gánh cả chục ảnh base64, nguyên nhân thường gặp nhất là
    /// body quá lớn — nên nó đáng được thử lại một lần bằng ngữ cảnh text, y như quirk ảnh ở trên.
    /// </summary>
    public static bool TransportFailed(LlmCallResult result) =>
        !result.IsSuccess && result.FailureKind == LlmFailureKind.Transport;

    /// <summary>
    /// Có nên thử lại lượt này mà KHÔNG kèm ảnh không: chỉ khi lượt đó thật sự có ảnh, và lý do hỏng là một
    /// trong hai thứ mà bỏ ảnh đi thì cứu được — endpoint từ chối content ảnh, hoặc request chết ở tầng
    /// transport (thường vì body quá lớn). Dùng chung cho cả đường stream lẫn đường json_schema để hai
    /// đường không bao giờ lệch nhau về hành vi này.
    /// </summary>
    public static bool ShouldRetryWithoutImages(LlmCallResult result, IEnumerable<ChatMessage> messages) =>
        (RejectedImageContent(result) || TransportFailed(result)) && ContainsImageContent(messages);

    public static bool ContainsImageContent(IEnumerable<ChatMessage> messages) =>
        messages.Any(HasImage);

    /// <summary>
    /// Bản sao hội thoại đã bỏ mọi phần ảnh, để thử lại trên NGỮ CẢNH TEXT thay vì hỏng cả lượt.
    ///
    /// Mỗi lượt từng có ảnh được đính thêm một dòng GHI ĐÈ, không phải chỉ lượt bị rỗng content. Lý do:
    /// phần text đi kèm ảnh do <see cref="ICOGenerator.Services.Requirements.SourceContextBuilder"/> dựng
    /// đã nói thẳng "kèm 12 hình dưới dạng ẢNH — xem nội dung ảnh đính kèm"; bỏ ảnh đi mà để nguyên câu đó
    /// là mời model bịa ra nội dung 12 tấm hình nó chưa từng thấy. Câu ghi đè phải đứng CUỐI để thắng
    /// những gì nói trước đó.
    /// </summary>
    public static List<ChatMessage> WithoutImageContent(IEnumerable<ChatMessage> messages) =>
        messages.Select(m =>
        {
            if (!HasImage(m))
                return m;

            var kept = m.Contents.Where(c => c is not DataContent and not UriContent).ToList();
            kept.Add(new TextContent(ImagesDroppedNotice));
            return new ChatMessage(m.Role, kept);
        }).ToList();

    /// <summary>Dòng ghi đè đính vào lượt bị gỡ ảnh — xem <see cref="WithoutImageContent"/>.</summary>
    public const string ImagesDroppedNotice =
        "\n\n[GHI ĐÈ MỌI CÂU Ở TRÊN VỀ ẢNH] Các ẢNH nhắc tới phía trên KHÔNG được gửi kèm lượt này "
        + "(endpoint không nhận ảnh, hoặc request quá lớn). Bạn CHƯA nhìn thấy tấm hình nào: tuyệt đối "
        + "không mô tả, không suy đoán, không kể lại nội dung của chúng. Chỉ dựa vào phần CHỮ, và nói rõ "
        + "với người dùng là phần hình chưa đọc được.";

    /// <summary>
    /// JSON mode bị từ chối thẳng nếu bản thân prompt không nhắc tới "json" (tài liệu của cả DeepSeek lẫn
    /// OpenAI đều ghi), nên kiểm tra TRƯỚC khi tốn một round-trip chắc chắn 400.
    /// </summary>
    public static bool MentionsJson(IEnumerable<ChatMessage> messages) =>
        messages.Any(m => m.Text.Contains("json", StringComparison.OrdinalIgnoreCase));

    private static bool HasImage(ChatMessage message) =>
        message.Contents.Any(c => c is DataContent or UriContent);
}
