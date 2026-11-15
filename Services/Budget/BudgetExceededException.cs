using ICOGenerator.Localization;
namespace ICOGenerator.Services.Budget;

/// <summary>
/// Ném bởi <see cref="IBudgetGuard"/> để CHẶN một lời gọi model TRƯỚC khi nó được gửi đi, khi tổng chi phí
/// trong kỳ đã chạm trần USD đã cấu hình. Mang theo scope + số liệu để thông báo rõ lý do và UI hiển thị được
/// vì sao công việc tạm dừng. Cố ý KHÔNG kế thừa các exception "lỗi gọi LLM" — đây là một quyết định chính
/// sách (đã đạt trần), không phải lỗi mạng/model, nên middleware không bọc lại thành "LLM call failed".
/// </summary>
public sealed class BudgetExceededException : Exception
{
    public BudgetScope Scope { get; }
    public decimal SpentUsd { get; }
    public decimal LimitUsd { get; }
    public BudgetPeriod Period { get; }

    public BudgetExceededException(BudgetScope scope, decimal spentUsd, decimal limitUsd, BudgetPeriod period)
        : base(BuildMessage(scope, spentUsd, limitUsd, period))
    {
        Scope = scope;
        SpentUsd = spentUsd;
        LimitUsd = limitUsd;
        Period = period;
    }

    private static string BuildMessage(BudgetScope scope, decimal spent, decimal limit, BudgetPeriod period)
    {
        var scopeText = scope == BudgetScope.System ? Loc.T("BudgetExceededException.BuildMessage") : Loc.T("BudgetExceededException.BuildMessage.2");
        var periodText = period switch
        {
            BudgetPeriod.Daily => Loc.T("BudgetExceededException.BuildMessage.3"),
            BudgetPeriod.Monthly => Loc.T("BudgetExceededException.BuildMessage.4"),
            _ => Loc.T("BudgetExceededException.BuildMessage.5")
        };
        return Loc.T("BudgetExceededException.BuildMessage.6", scopeText, periodText, spent, limit);
    }
}
