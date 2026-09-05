namespace ICOGenerator.Services.Requirements;

/// <summary>
/// CỬA DUY NHẤT chạy các vòng "học vào checklist BA" (<see cref="ICOGenerator.Domain.AgentChecklistItem"/>).
///
/// <para>
/// Cả ba đường học đều bắt đầu từ một CỔNG DUYỆT — duyệt Product Brief, duyệt bản demo, bác giả định ở
/// cổng xác nhận — nhưng cổng duyệt chạy đồng bộ trong request HTTP, nên không được phép gọi LLM tại đó:
/// đó đúng là lý do việc sinh AI Design Spec đã phải rời khỏi <c>ApproveRequirementUseCase</c>. Vì vậy mỗi
/// cổng chỉ ghi một HÀNG ĐỢI trên <see cref="ICOGenerator.Domain.Project"/> (vài UPDATE, trả về ngay), còn
/// việc chắt lọc thì <see cref="ICOGenerator.Services.Workflows.AgentTaskWorker"/> gọi vào đây khi nhận
/// task kế tiếp của dự án đó.
/// </para>
///
/// <para>
/// Worker chỉ biết một dòng gọi và không cần biết bước nào vừa được duyệt: mỗi service tự gác hàng đợi
/// của mình (không có gì trong hàng đợi ⇒ no-op, chỉ tốn một truy vấn) và tự <b>fail-open</b> (nuốt + log,
/// hàng đợi đứng yên, task sau gộp bù). Đó là điều kiện để chỗ này giữ được tính generic của worker.
/// </para>
/// </summary>
public class RequirementMemoryHarvester
{
    private readonly ChecklistGapMemoryService _checklistGap;
    private readonly PocFeedbackMemoryService _pocFeedback;
    private readonly SpecAssumptionMemoryService _specAssumption;

    public RequirementMemoryHarvester(
        ChecklistGapMemoryService checklistGap,
        PocFeedbackMemoryService pocFeedback,
        SpecAssumptionMemoryService specAssumption)
    {
        _checklistGap = checklistGap;
        _pocFeedback = pocFeedback;
        _specAssumption = specAssumption;
    }

    /// <summary>
    /// Chạy hết các hàng đợi học đang mở của một dự án. Thứ tự theo độ SẮC của bằng chứng giảm dần: giả
    /// định bị bác (người dùng chỉ thẳng chỗ hiểu sai) → ghi chú trên bản mô tả → ghi chú trên bản demo.
    /// Đường sắc hơn chạy trước thì bài học của nó vào bucket trước, và đường sau nhận chính nó trong
    /// "checklist đang dùng" nên không đề xuất lại cùng một ý.
    /// </summary>
    public async Task DrainAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await _specAssumption.TryHarvestAsync(projectId, cancellationToken);
        await _checklistGap.TryHarvestAsync(projectId, cancellationToken);
        await _pocFeedback.TryHarvestAsync(projectId, cancellationToken);
    }
}
