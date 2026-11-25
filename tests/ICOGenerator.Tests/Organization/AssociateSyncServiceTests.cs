using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Organization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Organization;

// Đồng bộ Associates: khớp theo PersonalNumber, giữ nguyên Id bản ghi đang có, người quay lại
// được bật lại kèm xoá ngày nghỉ cũ, và bước LeavingDate chỉ hỏi những người đã rời danh sách mà chưa có ngày.
public class AssociateSyncServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly FakeEmployeeApiClient _api = new();

    public AssociateSyncServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    private AssociateSyncService NewService() => new(NewDb(), _api, NullLogger<AssociateSyncService>.Instance);

    private async Task SeedAsync(params Associate[] associates)
    {
        await using var seed = NewDb();
        seed.Associates.AddRange(associates);
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task MapsFields_KeepsId_AndSoftDeletesMissing()
    {
        var keptId = Guid.NewGuid();
        await SeedAsync(
            new Associate { Id = keptId, PersonalNumber = "100", DisplayName = "Old Name" },
            new Associate { Id = Guid.NewGuid(), PersonalNumber = "999", DisplayName = "Gone" });

        _api.Employees =
        [
            new()
            {
                PersonalNumber = "100", EmployeeName = " Le Anh Hao ", Gender = 1, OrgUnitId = "50100",
                OrganizationUnit = "HcP/HRL", Email = "HAO@VN.BOSCH.COM", UserId = "LHN9HC", GlobalId = "119",
                Position = "Engineer", Costcenter = "0000183955", HiredDate = new DateTime(2025, 3, 12),
            },
            new() { PersonalNumber = "101", EmployeeName = "Nguyen Thi Le", Gender = 2 },
            new() { PersonalNumber = " ", EmployeeName = "Không mã" },
        ];

        var result = await NewService().SyncAsync();

        Assert.Equal(new SyncResult(Inserted: 1, Updated: 1, Deleted: 1), result);
        await using var db = NewDb();
        var kept = await db.Associates.SingleAsync(a => a.PersonalNumber == "100");
        Assert.Equal(keptId, kept.Id);
        Assert.Equal("Le Anh Hao", kept.DisplayName);
        Assert.Equal("1", kept.Gender);
        Assert.Equal("50100", kept.OrgUnitCode);
        Assert.Equal("LHN9HC", kept.UserId);
        Assert.Equal(new DateTime(2025, 3, 12), kept.HiredDate);

        Assert.Equal("2", (await db.Associates.SingleAsync(a => a.PersonalNumber == "101")).Gender);
        Assert.True((await db.Associates.SingleAsync(a => a.PersonalNumber == "999")).IsDelete);
    }

    [Fact]
    public async Task RehiredAssociate_IsReactivatedAndLosesOldLeavingDate()
    {
        await SeedAsync(new Associate
        {
            Id = Guid.NewGuid(), PersonalNumber = "100", DisplayName = "Le Anh Hao",
            IsDelete = true, LeavingDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _api.Employees = [new() { PersonalNumber = "100", EmployeeName = "Le Anh Hao" }];

        await NewService().SyncAsync();

        await using var db = NewDb();
        var associate = await db.Associates.SingleAsync();
        Assert.False(associate.IsDelete);
        Assert.Null(associate.LeavingDate);
    }

    [Fact]
    public async Task EmptyApiResponse_AbortsWithoutDeletingAnything()
    {
        await SeedAsync(new Associate { Id = Guid.NewGuid(), PersonalNumber = "100" });
        _api.Employees = [];

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewService().SyncAsync());

        await using var db = NewDb();
        Assert.False((await db.Associates.SingleAsync()).IsDelete);
    }

    [Fact]
    public async Task LeavingDates_OnlyAsksDeletedAssociatesWithoutDate_AndSkipsFailures()
    {
        var known = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            new Associate { Id = Guid.NewGuid(), PersonalNumber = "active" },
            new Associate { Id = Guid.NewGuid(), PersonalNumber = "has-date", IsDelete = true, LeavingDate = known },
            new Associate { Id = Guid.NewGuid(), PersonalNumber = "leaver", IsDelete = true },
            new Associate { Id = Guid.NewGuid(), PersonalNumber = "unknown", IsDelete = true },
            new Associate { Id = Guid.NewGuid(), PersonalNumber = "broken", IsDelete = true });
        _api.LeavingInfo["leaver"] = new EmployeeInfoResponse { LeavingDate = new DateTime(2026, 10, 31) };
        _api.FailingLeavingInfo.Add("broken");

        var filled = await NewService().SyncLeavingDatesAsync();

        Assert.Equal(1, filled);
        Assert.Equal(["broken", "leaver", "unknown"], _api.LeavingInfoRequests.OrderBy(x => x));
        await using var db = NewDb();
        Assert.Equal(new DateTime(2026, 10, 31), (await db.Associates.SingleAsync(a => a.PersonalNumber == "leaver")).LeavingDate);
        Assert.Null((await db.Associates.SingleAsync(a => a.PersonalNumber == "unknown")).LeavingDate);
    }

    [Fact]
    public async Task LeavingDates_EveryLookupFailing_FailsTheJob()
    {
        await SeedAsync(new Associate { Id = Guid.NewGuid(), PersonalNumber = "broken", IsDelete = true });
        _api.FailingLeavingInfo.Add("broken");

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewService().SyncLeavingDatesAsync());
    }
}
