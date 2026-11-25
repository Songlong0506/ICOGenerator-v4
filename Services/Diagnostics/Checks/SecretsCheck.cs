using ICOGenerator.Data;
using ICOGenerator.Localization;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.Json;

namespace ICOGenerator.Services.Diagnostics.Checks;

/// <summary>
/// Bí mật: có không, lấy từ ĐÂU, và khóa mã hóa có giải được ApiKey đã lưu không.
/// <para>
/// "Từ đâu" quan trọng ngang "có không": giá trị nằm trong <c>appsettings.json</c> là giá trị đã commit vào git
/// (khóa <c>Encryption:ApiKeyKey</c> trong repo coi như đã lộ). Trên IIS nạp chúng bằng biến môi trường
/// (<c>environmentVariables</c> trong <c>web.config</c> hoặc của application pool).
/// </para>
/// </summary>
public sealed class SecretsCheck : IEnvironmentCheck
{
    // Khóa chứa bí mật mà app đọc. Thêm bí mật mới vào cấu hình ⇒ thêm vào đây để nút Test Environment canh nó.
    private static readonly string[] SecretKeys =
    [
        "Encryption:ApiKeyKey",
        "EmployeeApi:ClientCredential:ClientSecret",
        "IdentityServer:ClientSecret",
        "Notifications:BoschEmail:ApiKey",
        "Notifications:Email:Password",
    ];

    private readonly IConfiguration _configuration;
    private readonly AppDbContext _db;

    public SecretsCheck(IConfiguration configuration, AppDbContext db)
    {
        _configuration = configuration;
        _db = db;
    }

    public string Group => Loc.T("EnvCheck.Secrets.Group");
    public TimeSpan Timeout => TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<EnvironmentCheckResult>> RunAsync(EnvironmentCheckContext context, CancellationToken cancellationToken)
    {
        var results = new List<EnvironmentCheckResult>();

        foreach (var key in SecretKeys)
        {
            var value = _configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                // Chỉ khóa mã hóa là bắt buộc; các bí mật khác thuộc tính năng tùy chọn, trống = tính năng đó tắt.
                if (key == "Encryption:ApiKeyKey")
                    results.Add(new(Group, key, EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Secrets.Missing"),
                        Loc.T("EnvCheck.Secrets.Missing.Fix", EnvironmentVariableName(key))));
                continue;
            }

            var source = SourceOf(key);
            if (source is JsonConfigurationProvider json
                && string.Equals(json.Source.Path, "appsettings.json", StringComparison.OrdinalIgnoreCase))
                results.Add(new(Group, key, EnvironmentCheckStatus.Warning, Loc.T("EnvCheck.Secrets.FromAppSettings"),
                    key == "Encryption:ApiKeyKey"
                        ? Loc.T("EnvCheck.Secrets.FromAppSettings.FixEncryption", EnvironmentVariableName(key))
                        : Loc.T("EnvCheck.Secrets.FromAppSettings.Fix", EnvironmentVariableName(key))));
            else
                results.Add(new(Group, key, EnvironmentCheckStatus.Pass,
                    Loc.T("EnvCheck.Secrets.Source", source?.ToString() ?? "?")));
        }

        // Converter của AppDbContext giải mã ApiKey lúc đọc và trả RỖNG khi giải không được (sai khóa) — nên một
        // model HTTP còn ApiKey rỗng sau khi đọc là: chưa nhập key, hoặc khóa hiện tại không phải khóa đã mã hóa nó.
        var models = await _db.AiModels.AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => new { x.ModelId, x.Endpoint, x.ApiKey })
            .ToListAsync(cancellationToken);
        var unreadable = models
            .Where(x => !ClaudeCodeCli.IsClaudeCodeEndpoint(x.Endpoint) && string.IsNullOrEmpty(x.ApiKey))
            .Select(x => x.ModelId)
            .ToList();

        results.Add(unreadable.Count == 0
            ? new(Group, Loc.T("EnvCheck.Secrets.StoredKeys"), EnvironmentCheckStatus.Pass,
                Loc.T("EnvCheck.Secrets.StoredKeys.Ok", models.Count))
            : new(Group, Loc.T("EnvCheck.Secrets.StoredKeys"), EnvironmentCheckStatus.Warning,
                Loc.T("EnvCheck.Secrets.StoredKeys.Unreadable", string.Join(", ", unreadable)),
                Loc.T("EnvCheck.Secrets.StoredKeys.Fix")));

        return results;
    }

    /// <summary>Provider CUỐI cùng có giá trị cho khóa — chính là provider thắng (provider sau đè provider trước).</summary>
    private IConfigurationProvider? SourceOf(string key)
    {
        if (_configuration is not IConfigurationRoot root)
            return null;
        return root.Providers.Reverse().FirstOrDefault(p => p.TryGet(key, out var v) && !string.IsNullOrWhiteSpace(v));
    }

    private static string EnvironmentVariableName(string key) => key.Replace(":", "__");
}
