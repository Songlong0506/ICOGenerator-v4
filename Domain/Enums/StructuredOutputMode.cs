using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Mức structured output mà endpoint của một model chấp nhận. Ba mức này là BA TẦNG NĂNG LỰC KHÁC NHAU, không
/// phải một cờ bật/tắt: có endpoint (DeepSeek) nhận <c>json_object</c> nhưng 400 với <c>json_schema</c>
/// ("This response_format type is unavailable now"), nên một <c>bool</c> không diễn tả nổi.
/// Lưu xuống DB dạng chuỗi (tên enum) nên ĐỪNG đổi tên các giá trị đã ghi.
/// </summary>
public enum StructuredOutputMode
{
    /// <summary>Không gửi <c>response_format</c>. Mặc định an toàn cho server local/model lạ: kết quả JSON được bóc từ văn xuôi bằng parser tay.</summary>
    [Description("Không dùng (parse văn xuôi)")]
    None,

    /// <summary>
    /// Gửi <c>response_format: {"type":"json_object"}</c> (JSON mode). API bảo đảm CÚ PHÁP JSON hợp lệ —
    /// hết cảnh model bọc rào <c>```json</c> hay thêm lời dẫn — nhưng KHÔNG bảo đảm đúng schema, nên parser
    /// tay vẫn là lưới đỡ. Chạy qua đường streaming nên vẫn stream token lên UI được.
    /// </summary>
    [Description("JSON mode (response_format: json_object)")]
    JsonObject,

    /// <summary>
    /// Gửi <c>response_format: {"type":"json_schema"}</c> sinh từ kiểu <c>T</c> (structured output đúng nghĩa).
    /// Chỉ endpoint OpenAI thật và vài server tương thích đủ mới nhận. KHÔNG streaming được nên
    /// <c>onToken</c> bị bỏ qua ở mức này.
    /// </summary>
    [Description("JSON Schema (response_format: json_schema)")]
    JsonSchema
}
