using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Organization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Organization;

// Đồng bộ OrgUnits từ employee-api: khớp theo mã, giữ nguyên Id của bản ghi đang có (Project.OrgUnitCode và
// bucket checklist tra theo mã, nhưng dữ liệu cũ phải sống sót qua lần sync đầu), xoá mềm/bật lại theo API,
// và tuyệt đối không xoá cả bảng khi API trả rỗng.
public class OrgUnitSyncServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly FakeEmployeeApiClient _api = new();

    public OrgUnitSyncServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    private Task<SyncResult> SyncAsync()
    {
        var db = NewDb();
        return new OrgUnitSyncService(db, _api, NullLogger<OrgUnitSyncService>.Instance).SyncAsync();
    }

    [Fact]
    public async Task InsertsUpdatesAndSoftDeletes()
    {
        var keptId = Guid.NewGuid();
        await using (var seed = NewDb())
        {
            seed.OrgUnits.Add(new OrgUnit { Id = keptId, OrgUnitCode = "50100", DisplayName = "HcP/OLD", TypeOrganize = "G" });
            seed.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "50999", DisplayName = "HcP/GONE" });
            await seed.SaveChangesAsync();
        }

        _api.OrgUnits =
        [
            new() { OrgUnitId = " 50100 ", Name = "HcP/HRL", TypeOrganize = "A", TargetResponsible = "50000", TrgtManagerLId = "900" },
            new() { OrgUnitId = "50101", Name = "HcP/HRL1", TypeOrganize = "G", TargetResponsible = "50100" },
            new() { OrgUnitId = "", Name = "Không mã" },
        ];

        var result = await SyncAsync();

        Assert.Equal(new SyncResult(Inserted: 1, Updated: 1, Deleted: 1), result);
        await using var db = NewDb();
        var kept = await db.OrgUnits.SingleAsync(u => u.OrgUnitCode == "50100");
        Assert.Equal(keptId, kept.Id);
        Assert.Equal("HcP/HRL", kept.DisplayName);
        Assert.Equal("A", kept.TypeOrganize);
        Assert.Equal("900", kept.TrgtManagerLId);
        Assert.Equal(OrganizationSyncJob.Actor, kept.UpdatedBy);

        var created = await db.OrgUnits.SingleAsync(u => u.OrgUnitCode == "50101");
        Assert.Equal("50100", created.TargetResponsible);
        Assert.Equal(OrganizationSyncJob.Actor, created.CreatedBy);

        Assert.True((await db.OrgUnits.SingleAsync(u => u.OrgUnitCode == "50999")).IsDelete);
    }

    [Fact]
    public async Task SecondRunWithSameData_ChangesNothing()
    {
        _api.OrgUnits = [new() { OrgUnitId = "50100", Name = "HcP/HRL", TypeOrganize = "A" }];

        await SyncAsync();
        var second = await SyncAsync();

        Assert.Equal(new SyncResult(0, 0, 0), second);
    }

    [Fact]
    public async Task UnitBackInApi_IsReactivated()
    {
        await using (var seed = NewDb())
        {
            seed.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "50100", DisplayName = "HcP/HRL", IsDelete = true });
            await seed.SaveChangesAsync();
        }
        _api.OrgUnits = [new() { OrgUnitId = "50100", Name = "HcP/HRL" }];

        var result = await SyncAsync();

        Assert.Equal(1, result.Updated);
        await using var db = NewDb();
        Assert.False((await db.OrgUnits.SingleAsync()).IsDelete);
    }

    [Fact]
    public async Task EmptyApiResponse_AbortsWithoutDeletingAnything()
    {
        await using (var seed = NewDb())
        {
            seed.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "50100", DisplayName = "HcP/HRL" });
            await seed.SaveChangesAsync();
        }
        _api.OrgUnits = [];

        await Assert.ThrowsAsync<InvalidOperationException>(SyncAsync);

        await using var db = NewDb();
        Assert.False((await db.OrgUnits.SingleAsync()).IsDelete);
    }
}
