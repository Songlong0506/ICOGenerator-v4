using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Application.Models;

/// <summary>
/// Một suất model trên màn AI Models. <see cref="AiModelId"/> null = chưa khai (lời gọi chạy model của agent
/// gọi nó). <see cref="AssignedModelInactive"/> = đã khai nhưng model đó đang tắt — suất đang lùi về model của
/// agent gọi, UI phải nói ra thay vì để người xem tưởng model ấy đang chạy.
/// </summary>
public record ModelSlotRow(
    ModelSlot Slot,
    Guid? AiModelId,
    bool AssignedModelInactive,
    string? UpdatedByUsername,
    DateTime? UpdatedAt);

/// <summary>
/// Khối "Model theo vai trò": mọi giá trị <see cref="ModelSlot"/> (kể cả suất chưa khai) và danh sách model chọn
/// được — model đang bật, cộng model đang gán mà đã tắt (để dropdown hiện đúng giá trị đang lưu).
/// </summary>
public record ModelSlotsVm(IReadOnlyList<ModelSlotRow> Slots, IReadOnlyList<AiModel> Models);
