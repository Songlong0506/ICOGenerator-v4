using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Model đang gán cho một <see cref="ModelSlot"/>. Nhiều nhất một dòng mỗi suất (<see cref="Slot"/> là khoá
/// chính). Không có dòng = suất chưa khai ⇒ lời gọi chạy model của agent gọi nó. FK <c>Cascade</c>: xoá model
/// thì dòng đi theo, suất lùi về "chưa khai" thay vì chặn việc xoá.
/// </summary>
public class ModelSlotAssignment
{
    public ModelSlot Slot { get; set; }
    public Guid AiModelId { get; set; }
    public AiModel AiModel { get; set; } = null!;
    public string? UpdatedByUsername { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
