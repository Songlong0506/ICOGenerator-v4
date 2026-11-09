using ICOGenerator.Services.Budget;

namespace ICOGenerator.Application.Settings;

public class AppSettingsVm
{
    public string ConnectionString { get; set; } = "";
    public string WorkspaceRootPath { get; set; } = "";

    /// <summary>One command per line, e.g. "dotnet" or "git status".</summary>
    public string AllowedCommands { get; set; } = "";

    /// <summary>One extension per line, e.g. ".cs".</summary>
    public string AllowedFileExtensions { get; set; } = "";

    public string PullRequestRemoteName { get; set; } = "origin";
    public string AllowedHosts { get; set; } = "*";

    // ── Budget (section "Budget" — xem BudgetPolicy) ──
    public bool BudgetEnabled { get; set; } = true;
    public BudgetPeriod BudgetPeriod { get; set; } = BudgetPeriod.Monthly;

    /// <summary>
    /// Trần USD dạng chuỗi số kiểu invariant ("12.5"). Cố ý KHÔNG bind thẳng <c>decimal</c>: model binding
    /// của MVC parse theo culture của server, ở culture dùng dấu phẩy thập phân "12.5" sẽ bị hiểu sai/rỗng.
    /// <see cref="UpdateAppSettingsUseCase"/> tự parse và chấp nhận cả "12,5". Rỗng = 0 = không giới hạn.
    /// </summary>
    public string BudgetSystemUsdLimit { get; set; } = "0";

    /// <inheritdoc cref="BudgetSystemUsdLimit"/>
    public string BudgetPerProjectUsdLimit { get; set; } = "0";
}
