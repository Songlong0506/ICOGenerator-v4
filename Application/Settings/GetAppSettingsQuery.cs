using System.Globalization;
using System.Text.Json.Nodes;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Settings;

namespace ICOGenerator.Application.Settings;

public class GetAppSettingsQuery
{
    private readonly AppSettingsFileStore _store;
    public GetAppSettingsQuery(AppSettingsFileStore store) => _store = store;

    public async Task<AppSettingsVm> ExecuteAsync()
    {
        var root = await _store.ReadAsync();

        return new AppSettingsVm
        {
            ConnectionString = root["ConnectionStrings"]?["DefaultConnection"]?.GetValue<string>() ?? "",
            WorkspaceRootPath = root["AgentWorkspace"]?["RootPath"]?.GetValue<string>() ?? "",
            AllowedCommands = JoinLines(root["AllowedCommands"]),
            AllowedFileExtensions = JoinLines(root["AllowedFileExtensions"]),
            PullRequestRemoteName = root["PullRequest"]?["RemoteName"]?.GetValue<string>() ?? "origin",
            AllowedHosts = root["AllowedHosts"]?.GetValue<string>() ?? "*",
            // Mặc định khớp đúng BudgetPolicy (Enabled=true, Monthly, 0/0) khi file chưa có section Budget.
            BudgetEnabled = ReadBool(root["Budget"]?["Enabled"], fallback: true),
            BudgetPeriod = Enum.TryParse<BudgetPeriod>(ReadString(root["Budget"]?["Period"]), ignoreCase: true, out var period)
                ? period
                : BudgetPeriod.Monthly,
            BudgetSystemUsdLimit = ReadDecimal(root["Budget"]?["SystemUsdLimit"]).ToString(CultureInfo.InvariantCulture),
            BudgetPerProjectUsdLimit = ReadDecimal(root["Budget"]?["PerProjectUsdLimit"]).ToString(CultureInfo.InvariantCulture)
        };
    }

    // appsettings.json có thể do người sửa tay: số/bool có khi được ghi dạng chuỗi ("50", "true") —
    // IConfiguration chấp nhận cả hai nên trang Settings cũng phải đọc được cả hai, không được ném lỗi.
    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value ? value.ToString() : null;

    private static bool ReadBool(JsonNode? node, bool fallback) =>
        bool.TryParse(ReadString(node), out var result) ? result : fallback;

    private static decimal ReadDecimal(JsonNode? node) =>
        decimal.TryParse(ReadString(node), NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;

    private static string JoinLines(JsonNode? node) =>
        node is JsonArray array
            ? string.Join("\n", array
                .Select(x => x?.GetValue<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            : "";
}
