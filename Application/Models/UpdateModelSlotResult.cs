namespace ICOGenerator.Application.Models;

public enum UpdateModelSlotResult
{
    Saved,
    UnknownSlot,
    /// <summary>Model không tồn tại hoặc đang tắt — từ chối thay vì lưu một suất sẽ lặng lẽ lùi về model của agent.</summary>
    ModelUnavailable
}
