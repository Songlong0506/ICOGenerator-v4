using ICOGenerator.Configuration;
using ICOGenerator.Data;
using ICOGenerator.Localization;
using ICOGenerator.Services.Organization;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Diagnostics.Checks;

/// <summary>
/// Đồng bộ OrgUnits/Associates từ employee-api: có bật không, xin được token không, và bảng OrgUnits có dữ liệu
/// chưa (tạo project cần một orgUnit — bảng trống là màn Projects không tạo được gì).
/// </summary>
public sealed class EmployeeApiCheck : IEnvironmentCheck
{
    private readonly EmployeeApiSettings _settings;
    private readonly ClientCredentialTokenProvider _tokens;
    private readonly AppDbContext _db;

    public EmployeeApiCheck(EmployeeApiSettings settings, ClientCredentialTokenProvider tokens, AppDbContext db)
    {
        _settings = settings;
        _tokens = tokens;
        _db = db;
    }

    public string Group => Loc.T("EnvCheck.EmployeeApi.Group");
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<EnvironmentCheckResult>> RunAsync(EnvironmentCheckContext context, CancellationToken cancellationToken)
    {
        var results = new List<EnvironmentCheckResult>();
        var sync = Loc.T("EnvCheck.EmployeeApi.Sync");

        if (!_settings.Enabled)
        {
            results.Add(new(Group, sync, EnvironmentCheckStatus.Info, Loc.T("EnvCheck.EmployeeApi.Disabled")));
        }
        else
        {
            try
            {
                await _tokens.GetTokenAsync(cancellationToken);
                results.Add(new(Group, sync, EnvironmentCheckStatus.Pass, Loc.T("EnvCheck.EmployeeApi.Token.Ok", _settings.ClientCredential.TokenUrl)));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                           or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                results.Add(new(Group, sync, EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.EmployeeApi.Token.Failed"),
                    Loc.T("EnvCheck.EmployeeApi.Token.Fix"), ex.GetBaseException().Message));
            }
        }

        var orgUnits = await _db.OrgUnits.CountAsync(x => !x.IsDelete, cancellationToken);
        results.Add(orgUnits > 0
            ? new(Group, "OrgUnits", EnvironmentCheckStatus.Pass, Loc.T("EnvCheck.EmployeeApi.OrgUnits.Ok", orgUnits))
            : new(Group, "OrgUnits", EnvironmentCheckStatus.Warning, Loc.T("EnvCheck.EmployeeApi.OrgUnits.Empty"),
                Loc.T("EnvCheck.EmployeeApi.OrgUnits.Fix")));

        return results;
    }
}
