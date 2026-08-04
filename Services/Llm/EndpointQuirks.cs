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
    /// Lời gọi chết ở TẦNG VẬN CHUYỂN — không nhận được bất kỳ HTTP status nào: HttpClient ném
    /// HttpRequestException ("An error occurred while sending the request"), SDK OpenAI tự thử lại rồi gom
    /// thành AggregateException "Retry failed after N tries". Với request MANG ẢNH, nguyên nhân thường gặp
    /// nhất là body base64 vượt giới hạn kích thước của endpoint hoặc proxy/gateway đứng trước nó (nginx
    /// <c>client_max_body_size</c> mặc định 1MB, proxy công ty…) — chúng RESET kết nối thay vì trả 413 tử
    /// tế, nên phía client chỉ còn dấu vết này để nhận ra. Phân biệt với timeout của chính app (deadline
    /// <c>Llm:RequestTimeoutSeconds</c>) vốn được map thành thông điệp riêng kèm status null.
    /// </summary>
    public static bool TransportSendFailure(LlmCallResult result) =>
        !result.IsSuccess && result.HttpStatusCode is null
        && (result.ResponseText.Contains("while sending the request", StringComparison.OrdinalIgnoreCase)
            || result.ResponseText.Contains("Retry failed after", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Endpoint không cài đặt mức <c>response_format</c> được xin sẽ trả 400 nêu đúng tên tham số —
    /// DeepSeek: <c>"This response_format type is unavailable now"</c>; server tự host thường là
    /// <c>"response_format is not supported"</c>.
    /// </summary>
    public static bool RejectedResponseFormat(LlmCallResult result) =>
        !result.IsSuccess && result.ResponseText.Contains("response_format", StringComparison.OrdinalIgnoreCase);

    public static bool ContainsImageContent(IEnumerable<ChatMessage> messages) =>
        messages.Any(HasImage);

    /// <summary>
    /// Bản sao hội thoại đã bỏ mọi phần ảnh, để thử lại trên NGỮ CẢNH TEXT thay vì hỏng cả lượt. Lượt nào
    /// chỉ có ảnh được thay bằng một dòng ghi chú, vì message rỗng content thì endpoint cũng từ chối.
    /// </summary>
    public static List<ChatMessage> WithoutImageContent(IEnumerable<ChatMessage> messages) =>
        messages.Select(m =>
        {
            if (!HasImage(m))
                return m;

            var kept = m.Contents.Where(c => c is not DataContent and not UriContent).ToList();
            if (kept.Count == 0)
                kept.Add(new TextContent("(ảnh đính kèm bị bỏ qua vì model không nhận ảnh)"));
            return new ChatMessage(m.Role, kept);
        }).ToList();

    /// <summary>
    /// JSON mode bị từ chối thẳng nếu bản thân prompt không nhắc tới "json" (tài liệu của cả DeepSeek lẫn
    /// OpenAI đều ghi), nên kiểm tra TRƯỚC khi tốn một round-trip chắc chắn 400.
    /// </summary>
    public static bool MentionsJson(IEnumerable<ChatMessage> messages) =>
        messages.Any(m => m.Text.Contains("json", StringComparison.OrdinalIgnoreCase));

    private static bool HasImage(ChatMessage message) =>
        message.Contents.Any(c => c is DataContent or UriContent);
}
