namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Loại của một <see cref="WorkflowRun"/> — cột phân loại DUY NHẤT để code rẽ nhánh theo run. <c>WorkflowRun.Name</c>
/// chỉ là nhãn cho người đọc (panel tiến độ), đổi chữ thoải mái mà không gãy logic. Lưu DB dạng chuỗi ⇒ đừng
/// đổi tên giá trị đã có dữ liệu.
/// </summary>
public enum WorkflowRunKind
{
    /// <summary>Vòng "Write Requirement": BA soạn/cập nhật bản nháp Product Brief từ hội thoại.</summary>
    RequirementDraft = 1,

    /// <summary>Sinh AI Design Spec cho một phiên bản requirement vừa duyệt.</summary>
    RequirementDesignSpec = 2,

    /// <summary>Pipeline delivery (<c>DeliveryPipeline.Steps</c>): từ AI Design Spec tới POC, tài liệu kỹ thuật, PR.</summary>
    Delivery = 3
}
