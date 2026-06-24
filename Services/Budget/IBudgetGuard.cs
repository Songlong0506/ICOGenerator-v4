namespace ICOGenerator.Services.Budget;

/// <summary>
/// Chốt chặn chi phí: kiểm tra TRƯỚC mỗi lời gọi model rằng tổng chi phí tích luỹ trong kỳ chưa chạm trần USD.
/// Được gọi tại chokepoint duy nhất mà mọi lời gọi LLM đi qua (<c>ModelCallLoggingChatClient</c>), nên áp cho
/// cả đường agent (vòng tool tự động) lẫn đường BA (chat đồng bộ). Vượt trần ⇒ ném
/// <see cref="BudgetExceededException"/> để chặn lời gọi thay vì tiếp tục đốt tiền.
/// </summary>
public interface IBudgetGuard
{
    /// <summary>
    /// Ném <see cref="BudgetExceededException"/> nếu chi phí trong kỳ đã chạm trần toàn hệ thống hoặc trần của
    /// <paramref name="projectId"/>. Không có trần nào cấu hình ⇒ trả về ngay, không truy vấn DB.
    /// </summary>
    Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default);
}
