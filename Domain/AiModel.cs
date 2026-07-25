using System.ComponentModel.DataAnnotations;
namespace ICOGenerator.Domain;

public class AiModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(200)] public string ModelId { get; set; } = string.Empty;
    [MaxLength(500)] public string Endpoint { get; set; } = string.Empty;
    [MaxLength(1000)] public string ApiKey { get; set; } = string.Empty;
    public int ContextWindow { get; set; } = 128000;
    public decimal InputPricePerMillionTokens { get; set; }
    public decimal OutputPricePerMillionTokens { get; set; }
    public bool IsActive { get; set; } = true;
    // Model có nhận input ảnh (vision/multimodal) không. Chỉ khi true thì tài liệu nguồn dạng ảnh (và trang
    // PDF scan đã render) mới được gửi cho model; model text-only chỉ nhận phần text bóc từ PDF.
    public bool SupportsVision { get; set; } = false;
    // Model có hỗ trợ structured output (tham số response_format: json_schema của OpenAI) không. OPT-IN theo
    // từng model, MẶC ĐỊNH TẮT vì nhiều server OpenAI-compatible/local từ chối tham số này. Khi true, các lời
    // gọi BA trả JSON dùng structured output thay vì parse văn xuôi; parser tay vẫn là fallback nếu JSON không
    // khớp schema. Model text-only để false thì giữ nguyên đường text + parser cũ.
    public bool SupportsStructuredOutput { get; set; } = false;
    // Username (claim Name) của người tạo model này qua màn hình quản trị Models. Nullable để tương thích các
    // model seed sẵn (DbInitializer) — chúng coi như do hệ thống tạo, không có chủ. Dùng để biết "ai đã tạo model".
    [MaxLength(100)] public string? CreatedByUsername { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
