using ICOGenerator.Data;
using ICOGenerator.Localization;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Diagnostics.Checks;

/// <summary>
/// Kết nối DB thật (mở một connection), provider đang dùng và migration còn treo. Lỗi hay gặp nhất trên IIS:
/// connection string <c>Trusted_Connection=True</c> đăng nhập SQL Server bằng DANH TÍNH APPLICATION POOL, không
/// phải tài khoản của người cài — phải tạo login cho <c>IIS APPPOOL\&lt;tên pool&gt;</c>.
/// </summary>
public sealed class DatabaseCheck : IEnvironmentCheck
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _environment;

    public DatabaseCheck(AppDbContext db, IWebHostEnvironment environment)
    {
        _db = db;
        _environment = environment;
    }

    public string Group => Loc.T("EnvCheck.Database.Group");
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<EnvironmentCheckResult>> RunAsync(EnvironmentCheckContext context, CancellationToken cancellationToken)
    {
        var results = new List<EnvironmentCheckResult>();
        var isSqlite = _db.Database.IsSqlite();
        var connection = _db.Database.GetDbConnection();
        var target = $"{connection.DataSource} / {connection.Database}";

        results.Add(isSqlite && !_environment.IsDevelopment()
            ? new(Group, Loc.T("EnvCheck.Database.Provider"), EnvironmentCheckStatus.Warning,
                Loc.T("EnvCheck.Database.Provider.SqliteOnServer"), Loc.T("EnvCheck.Database.Provider.SqliteOnServer.Fix"))
            : new(Group, Loc.T("EnvCheck.Database.Provider"), EnvironmentCheckStatus.Info,
                $"{_db.Database.ProviderName} · {target}"));

        try
        {
            await _db.Database.OpenConnectionAsync(cancellationToken);
            results.Add(new(Group, Loc.T("EnvCheck.Database.Connect"), EnvironmentCheckStatus.Pass,
                Loc.T("EnvCheck.Database.Connect.Ok", target)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            results.Add(new(Group, Loc.T("EnvCheck.Database.Connect"), EnvironmentCheckStatus.Fail,
                Loc.T("EnvCheck.Database.Connect.Failed", target),
                Loc.T("EnvCheck.Database.Connect.Fix", Environment.UserName),
                ex.GetBaseException().Message));
            return results;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }

        // Sqlite dựng schema bằng EnsureCreated, không có lịch sử migration để so.
        if (!isSqlite)
        {
            var pending = (await _db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            results.Add(pending.Count == 0
                ? new(Group, Loc.T("EnvCheck.Database.Migrations"), EnvironmentCheckStatus.Pass, Loc.T("EnvCheck.Database.Migrations.Ok"))
                : new(Group, Loc.T("EnvCheck.Database.Migrations"), EnvironmentCheckStatus.Warning,
                    Loc.T("EnvCheck.Database.Migrations.Pending", pending.Count),
                    Loc.T("EnvCheck.Database.Migrations.Fix"), string.Join(", ", pending)));
        }

        return results;
    }
}
