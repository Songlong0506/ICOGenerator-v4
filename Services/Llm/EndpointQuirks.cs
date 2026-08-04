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
    /// Request KHÔNG tới được model: chết ở tầng truyền tải, không có mã HTTP nào (xem
    /// <see cref="LlmCallResult.TransportFailure"/>). Với một lượt CÓ ẢNH, thủ phạm gần như luôn là KÍCH
    /// THƯỚC body — ảnh đi trên dây dưới dạng base64 nên một tài liệu Word vài hình chụp màn hình đẩy
    /// request lên hàng chục MB, vượt trần body của gateway/proxy đứng trước endpoint (nginx mặc định
    /// 1MB); bên kia đóng thẳng kết nối nên không có phản hồi nào để đọc lý do. Lượt chat text thuần chỉ
    /// vài chục KB nên vẫn chạy ngon — đó chính là hình dạng "gửi tin thì được, đính kèm file thì lỗi".
    /// Bỏ ảnh gửi lại là cách duy nhất còn cứu được lượt đó.
    /// </summary>
    public static bool RequestNeverReachedModel(LlmCallResult result) =>
        !result.IsSuccess && result.TransportFailure;

    public static bool ContainsImageContent(IEnumerable<ChatMessage> messages) =>
        messages.Any(HasImage);

    /// <summary>
    /// Bản sao hội thoại đã bỏ mọi phần ảnh, để thử lại trên NGỮ CẢNH TEXT thay vì hỏng cả lượt. Lượt nào
    /// chỉ có ảnh được thay bằng một dòng ghi chú, vì message rỗng content thì endpoint cũng từ chối.
    ///
    /// Message nào MẤT ảnh đều được đính thêm một dòng ĐÍNH CHÍNH. Bắt buộc, vì phần chữ do
    /// <c>SourceContextBuilder</c> dựng đã trót nói "kèm 12 hình dưới dạng ẢNH — xem nội dung ảnh đính
    /// kèm": để nguyên câu đó mà không gửi tấm nào là mời model bịa ra nội dung 12 hình không tồn tại, rồi
    /// bản bịa ấy chảy thẳng vào Product Brief. Đính chính đứng CUỐI message để nó ghi đè ấn tượng của
    /// những câu phía trên.
    /// </summary>
    public static List<ChatMessage> WithoutImageContent(IEnumerable<ChatMessage> messages) =>
        messages.Select(m =>
        {
            if (!HasImage(m))
                return m;

            var kept = m.Contents.Where(c => c is not DataContent and not UriContent).ToList();
            kept.Add(new TextContent(
                "\n\n[ĐÍNH CHÍNH — ĐỌC TRƯỚC KHI TRẢ LỜI] Các ẢNH nhắc tới ở trên rốt cuộc KHÔNG gửi kèm "
                + "được lượt này (endpoint không nhận ảnh, hoặc gói tin quá lớn). Bạn KHÔNG nhìn thấy hình "
                + "nào cả. TUYỆT ĐỐI không mô tả, không suy đoán nội dung bất kỳ hình nào: chỉ dùng phần "
                + "CHỮ có ở trên, và nói thẳng với người dùng rằng phần hình chưa đọc được, mời họ gõ lại "
                + "hoặc gửi từng ảnh riêng."));
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
