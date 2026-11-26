using System.ComponentModel.DataAnnotations;
using ICOGenerator.Domain.Enums;
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
    // Đơn giá token input được provider phục vụ từ CACHE prompt (OpenAI/DeepSeek: rẻ hơn giá input ~10 lần).
    // 0 = CHƯA KHAI BÁO, không phải "miễn phí": khi đó chi phí tính theo giá input đầy đủ, xem LlmPrice.
    public decimal CachedInputPricePerMillionTokens { get; set; }
    public bool IsActive { get; set; } = true;
    // Model có nhận input ảnh (vision/multimodal) không. Chỉ khi true thì tài liệu nguồn dạng ảnh (và trang
    // PDF scan đã render) mới được gửi cho model; model text-only chỉ nhận phần text bóc từ PDF.
    public bool SupportsVision { get; set; } = false;
    // Mức response_format mà endpoint của model này chấp nhận. OPT-IN theo từng model, MẶC ĐỊNH None vì nhiều
    // server OpenAI-compatible/local từ chối tham số response_format. Không phải bool: DeepSeek nhận
    // json_object nhưng 400 với json_schema, nên hai mức đó phải tách ra (xem StructuredOutputMode).
    // Ở mọi mức, parser tay vẫn là fallback khi JSON không khớp kiểu mong đợi.
    public StructuredOutputMode StructuredOutputMode { get; set; } = StructuredOutputMode.None;
    // Tắt việc XIN prompt cache (cache_control) cho model này. Chỉ có tác dụng với Claude trên Vertex AI — nơi
    // cache phải xin tường minh và mỗi token GHI vào cache bị tính 1,25× giá input. Gateway rải request qua nhiều
    // backend thì lượt sau không bao giờ đọc lại được (log "0 read, N written" ở mọi lời gọi) và xin cache chỉ
    // làm prompt đắt thêm 25%. Mặc định false (= vẫn xin) — đặt tên phủ định để checkbox bỏ trống trên form
    // bind đúng về mặc định. OpenAI tự cache, ghi không tốn thêm, nên cờ này không đụng tới đường đó.
    public bool DisablePromptCache { get; set; }
    // Gửi header sticky session của Bosch Model Farm (x-bmf-sticky-session-instance) để mọi lời gọi cùng một
    // project về CÙNG một backend — điều kiện để prompt cache Claude trên Vertex đọc lại được (thiếu nó BMF chia
    // round-robin và lượt sau gần như không bao giờ trúng). Chỉ có tác dụng với Claude trên Vertex qua BMF; BMF
    // gọi đây là tính năng THỬ NGHIỆM và cảnh báo dễ dính 429 hơn vì tải dồn vào một instance. Mặc định tắt.
    public bool StickySessionRouting { get; set; }
    // Username (claim Name) của người tạo model này qua màn hình quản trị Models. Nullable để tương thích các
    // model seed sẵn (DbInitializer) — chúng coi như do hệ thống tạo, không có chủ. Dùng để biết "ai đã tạo model".
    [MaxLength(100)] public string? CreatedByUsername { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Nhãn hiển thị ở mọi dropdown chọn model: "ModelId (endpoint rút gọn)". Cùng một ModelId có thể khai báo
    // hai lần trên hai endpoint khác nhau (vd. claude-haiku qua Vertex và qua gateway khác) — chỉ hiện ModelId
    // thì hai dòng giống hệt, chọn nhầm là gán agent sang provider/giá khác mà không biết.
    public static string DisplayLabel(string modelId, string? endpoint)
    {
        var short_ = ShortEndpoint(endpoint);
        return short_.Length == 0 ? modelId : $"{modelId} ({short_})";
    }

    public string DisplayLabel() => DisplayLabel(ModelId, Endpoint);

    // Endpoint rút gọn cho nhãn: bỏ scheme và query string (api-version… lặp lại ở mọi model, không phân biệt
    // được gì), bỏ "/" cuối. Quá dài thì giữ host + "…" + đoạn path cuối — đoạn phân biệt nhất (tên deployment,
    // publisher). URL không parse được thì trả nguyên chuỗi.
    public const int ShortEndpointMaxLength = 60;

    public static string ShortEndpoint(string? endpoint)
    {
        var raw = endpoint?.Trim() ?? string.Empty;
        if (raw.Length == 0) return string.Empty;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return raw;

        var path = uri.AbsolutePath.TrimEnd('/');
        var full = uri.Authority + path;
        if (full.Length <= ShortEndpointMaxLength) return full;

        var lastSegment = path[(path.LastIndexOf('/') + 1)..];
        return lastSegment.Length == 0 ? uri.Authority : $"{uri.Authority}/…/{lastSegment}";
    }
}
