namespace ICOGenerator.Services.Budget;

/// <summary>Phạm vi của trần ngân sách bị vượt — dùng cho thông báo và <see cref="BudgetExceededException"/>.</summary>
public enum BudgetScope
{
    /// <summary>Tổng chi phí của TẤT CẢ project trong kỳ.</summary>
    System,

    /// <summary>Chi phí của riêng MỘT project trong kỳ.</summary>
    Project
}
