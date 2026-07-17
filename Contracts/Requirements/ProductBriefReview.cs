namespace ICOGenerator.Contracts.Requirements;

// Kết quả vòng TỰ SOÁT bản nháp Product Brief (chạy sau khi soạn xong, trước khi ghi file): danh sách
// vấn đề thực chất so với hội thoại (bỏ sót yêu cầu, sai lệch, bịa thêm, thiếu mục…). Issues rỗng = bản
// nháp đạt, dùng luôn; có vấn đề = chạy đúng MỘT vòng sửa rồi dùng bản sửa. Xem ProductBriefDraftService.
public class ProductBriefReview
{
    public List<string> Issues { get; set; } = new();

    // Fail-open: soát lỗi/parse hỏng thì coi như bản nháp đạt để vòng tự soát không bao giờ chặn việc
    // sinh tài liệu vì một lỗi phụ.
    public static ProductBriefReview PassDefault => new();
}
