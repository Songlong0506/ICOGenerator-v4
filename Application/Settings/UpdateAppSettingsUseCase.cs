using System.Globalization;
using System.Text.Json.Nodes;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Settings;

namespace ICOGenerator.Application.Settings;

public class UpdateAppSettingsUseCase
{
    private readonly AppSettingsFileStore _store;
    private readonly GetAppSettingsQuery _getAppSettings;
    private readonly IAuditLogger _audit;

    public UpdateAppSettingsUseCase(AppSettingsFileStore store, GetAppSettingsQuery getAppSettings, IAuditLogger audit)
    {
        _store = store;
        _getAppSettings = getAppSettings;
        _audit = audit;
    }

    /// <summary>Returns an error message, or null when the settings were saved.</summary>
    public async Task<string?> ExecuteAsync(AppSettingsVm input)
    {
        if (string.IsNullOrWhiteSpace(input.ConnectionString))
            return "Connection string is required.";

        if (string.IsNullOrWhiteSpace(input.WorkspaceRootPath))
            return "Workspace root path is required.";

        if (!TryParseUsd(input.BudgetSystemUsdLimit, out var systemUsdLimit))
            return "Trần ngân sách toàn hệ thống phải là số USD không âm (0 = không giới hạn).";

        if (!TryParseUsd(input.BudgetPerProjectUsdLimit, out var perProjectUsdLimit))
            return "Trần ngân sách mỗi project phải là số USD không âm (0 = không giới hạn).";

        if (!Enum.IsDefined(input.BudgetPeriod))
            return "Kỳ tính ngân sách không hợp lệ.";

        // Đọc trạng thái TRƯỚC khi ghi để audit log so sánh được. ConnectionString sẽ bị AuditLogger tự che.
        var before = await _getAppSettings.ExecuteAsync();

        await _store.UpdateAsync(root =>
        {
            SetNested(root, "ConnectionStrings", "DefaultConnection", input.ConnectionString.Trim());
            SetNested(root, "AgentWorkspace", "RootPath", input.WorkspaceRootPath.Trim());
            SetNested(root, "PullRequest", "RemoteName",
                string.IsNullOrWhiteSpace(input.PullRequestRemoteName) ? "origin" : input.PullRequestRemoteName.Trim());

            root["AllowedCommands"] = ToJsonArray(SplitLines(input.AllowedCommands));
            root["AllowedFileExtensions"] = ToJsonArray(SplitLines(input.AllowedFileExtensions)
                .Select(x => x.StartsWith('.') ? x : "." + x)
                .Select(x => x.ToLowerInvariant()));

            root["AllowedHosts"] = string.IsNullOrWhiteSpace(input.AllowedHosts) ? "*" : input.AllowedHosts.Trim();

            // Chỉ ghi 4 key của Budget, giữ nguyên "_note" và mọi key khác trong section.
            if (root["Budget"] is not JsonObject budget)
                root["Budget"] = budget = new JsonObject();
            budget["Enabled"] = input.BudgetEnabled;
            budget["Period"] = input.BudgetPeriod.ToString();
            budget["SystemUsdLimit"] = systemUsdLimit;
            budget["PerProjectUsdLimit"] = perProjectUsdLimit;
        });

        await _audit.LogAsync(AuditCategory.Settings, AuditAction.Update, "AppSettings",
            "Cập nhật cấu hình ứng dụng", before: before, after: input);
        return null;
    }

    /// <summary>
    /// Parse trần USD không phụ thuộc culture: chấp nhận "12.5" lẫn "12,5"; rỗng = 0 (không giới hạn).
    /// Số âm bị từ chối thay vì lặng lẽ coi là 0 như <see cref="BudgetPolicy"/> — người dùng gõ âm là gõ nhầm.
    /// </summary>
    internal static bool TryParseUsd(string? value, out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var normalized = value.Trim().Replace(" ", "").Replace("$", "").Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result);
    }

    private static void SetNested(JsonObject root, string section, string key, string value)
    {
        if (root[section] is not JsonObject obj)
            root[section] = obj = new JsonObject();

        obj[key] = value;
    }

    private static IEnumerable<string> SplitLines(string value) => value
        .Split('\n')
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static JsonArray ToJsonArray(IEnumerable<string> values) =>
        new(values.Select(x => (JsonNode)x).ToArray());
}
