using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// "Suất model" dùng chung: một vai của MODEL (không phải của agent) mà nhiều lời gọi phụ có thể xin, cấu
/// hình một lần ở màn AI Models (<see cref="ModelSlotAssignment"/>). Chưa khai = mỗi lời gọi chạy model của
/// agent gọi nó. Lời gọi nào được xin suất nào là quyết định ở ĐIỂM GỌI trong code (kèm lý do), không phải ở
/// chỗ đặt file prompt hay ở cấu hình — xem <c>Services/Llm/ModelSlotResolver</c>.
/// Lưu DB dạng chuỗi ⇒ không đổi tên giá trị đã có.
/// </summary>
public enum ModelSlot
{
    /// <summary>Lượt trọng tài: chỉ PHÁN, đầu ra vài chục token — chỗ đầu tiên đáng thử một model rẻ hơn.</summary>
    [Description("Trọng tài")]
    Judge = 1
}
