namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Bộ QUY ƯỚC TRÌNH BÀY của MỘT dự án: các góp ý về giao diện mà người review đã ghim trên bản demo và
/// đội Dev đã sửa theo, được chắt lọc thành phát biểu dùng lại được
/// (<c>Prompts/BusinessAnalyst/poc-ui-convention.v1.md</c>).
///
/// Vì sao cần một bộ riêng thay vì để nguyên trong <c>poc-demo.html</c>: đường "Nhờ đội Dev chỉnh bản
/// demo" chỉ vá HTML, mà mỗi vòng dựng POC MỚI (sau khi tài liệu được sửa và duyệt lại) ghi đè cả file
/// đó về shell template. Không có bộ này thì mọi góp ý giao diện đã được chấp nhận đều mất trắng và
/// người review phải ghim lại từ đầu trên một bản demo trông y hệt lần trước.
/// Xem <c>Services/Requirements/PocUiConventionService</c>.
/// </summary>
public class PocUiConventionSet
{
    public List<PocUiConvention> Conventions { get; set; } = new();
}

public class PocUiConvention
{
    /// <summary>Mã hiển thị dạng <c>UI-n</c>, do service đánh lại theo thứ tự lưu — model không tự đặt.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Phát biểu quy ước, diễn đạt ĐỘC LẬP khỏi bản demo hiện tại để còn áp dụng được ở vòng sau.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Màn hình mà quy ước gắn vào (nguyên văn tên trong spec), rỗng nếu áp dụng cho toàn bộ bản demo.
    /// Vòng dựng sau chỉ áp dụng khi màn hình này còn trong spec — xem <c>BuildPromptBlock</c>.
    /// </summary>
    public string Screen { get; set; } = string.Empty;

    /// <summary>
    /// Trích dẫn ghi chú gốc. Đây là chỗ CUỐI CÙNG còn nhìn thấy nó: bản demo mang thay đổi ấy sẽ bị dựng
    /// lại, và <c>PocComment</c> tương ứng đã chuyển sang <c>Addressed</c> nên không quay lại vòng review.
    /// </summary>
    public string SourceComment { get; set; } = string.Empty;

    /// <summary>Thời điểm chắt lọc — dùng để giữ lại các quy ước MỚI NHẤT khi bộ chạm trần.</summary>
    public DateTime CapturedAtUtc { get; set; }
}
