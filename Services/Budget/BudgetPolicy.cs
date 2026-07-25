namespace ICOGenerator.Services.Budget;

/// <summary>
/// Cấu hình trần ngân sách LLM (config-bound, testable).
///
///   "Budget": { "Enabled": true, "Period": "Monthly", "SystemUsdLimit": 50, "PerProjectUsdLimit": 10 }
///
/// Hai trần độc lập, đơn vị USD: <see cref="SystemUsdLimit"/> (tổng mọi project) và
/// <see cref="PerProjectUsdLimit"/> (mỗi project). Đặt 0 = KHÔNG giới hạn scope đó. Mặc định cả hai = 0 nên
/// tính năng là OPT-IN: chưa đặt trần thì hành vi giữ nguyên (không chặn gì). <see cref="Period"/> quyết định
/// cửa sổ cộng dồn (mặc định theo tháng, khớp chu kỳ hoá đơn).
/// </summary>
public sealed class BudgetPolicy
{
    public bool Enabled { get; }
    public BudgetPeriod Period { get; }
    public decimal SystemUsdLimit { get; }
    public decimal PerProjectUsdLimit { get; }

    public BudgetPolicy(IConfiguration configuration)
        : this(
            configuration.GetValue("Budget:Enabled", true),
            configuration.GetValue("Budget:Period", BudgetPeriod.Monthly),
            configuration.GetValue("Budget:SystemUsdLimit", 0m),
            configuration.GetValue("Budget:PerProjectUsdLimit", 0m))
    {
    }

    public BudgetPolicy(bool enabled, BudgetPeriod period, decimal systemUsdLimit, decimal perProjectUsdLimit)
    {
        Enabled = enabled;
        Period = period;
        // Trần âm vô nghĩa — coi như không giới hạn (0).
        SystemUsdLimit = Math.Max(0m, systemUsdLimit);
        PerProjectUsdLimit = Math.Max(0m, perProjectUsdLimit);
    }

    /// <summary>
    /// Có trần nào thực sự đang áp không. Dùng để <see cref="BudgetGuard"/> bỏ qua hẳn truy vấn DB trên
    /// hot-path (mỗi lời gọi model đều đi qua guard) khi chưa cấu hình trần nào.
    /// </summary>
    public bool HasAnyLimit => Enabled && (SystemUsdLimit > 0 || PerProjectUsdLimit > 0);

    /// <summary>
    /// Mốc bắt đầu (bao gồm, UTC) của cửa sổ tính chi phí cho kỳ hiện tại. <see cref="BudgetPeriod.Total"/>
    /// trả <see cref="DateTime.MinValue"/> (tính tất cả). Truyền <paramref name="nowUtc"/> để test tất định.
    /// </summary>
    public DateTime WindowStart(DateTime nowUtc) => Period switch
    {
        BudgetPeriod.Daily => new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc),
        BudgetPeriod.Monthly => new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        _ => DateTime.MinValue
    };
}
