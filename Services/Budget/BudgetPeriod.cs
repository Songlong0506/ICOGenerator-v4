namespace ICOGenerator.Services.Budget;

/// <summary>
/// Khoảng thời gian tính dồn chi phí cho trần ngân sách. Quyết định mốc bắt đầu cửa sổ mà
/// <see cref="BudgetGuard"/> cộng chi phí để so với trần.
/// </summary>
public enum BudgetPeriod
{
    /// <summary>Cộng dồn từ trước tới nay — trần KHÔNG tự reset (chạm trần là dừng vĩnh viễn cho tới khi tăng trần).</summary>
    Total,

    /// <summary>Reset theo ngày (UTC): chỉ tính các lời gọi từ 00:00 hôm nay.</summary>
    Daily,

    /// <summary>Reset theo tháng (UTC): chỉ tính các lời gọi từ ngày 1 của tháng hiện tại. Khớp chu kỳ hoá đơn.</summary>
    Monthly
}
