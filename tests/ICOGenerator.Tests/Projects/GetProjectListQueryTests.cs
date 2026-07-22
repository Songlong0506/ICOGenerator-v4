using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Identity;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Trang Projects/Index lọc theo chủ sở hữu: User thường (canViewAll=false) chỉ thấy project mình tạo;
// Admin/TeamDev (canViewAll=true) thấy tất cả. Các project cũ không có chủ (CreatedByUsername=null) chỉ
// hiện cho người xem-tất-cả.
public class GetProjectListQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public GetProjectListQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNotViewAll_ReturnsOnlyOwnProjects()
    {
        await SeedProjectsAsync();

        await using var db = NewDb();
        var page = await NewQuery(db).ExecuteAsync(username: "alice", canViewAll: false);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("Alice's project", Assert.Single(page.Items).Project.Name);
    }

    [Fact]
    public async Task ExecuteAsync_WhenViewAll_ReturnsAllProjectsIncludingOwnerless()
    {
        await SeedProjectsAsync();

        await using var db = NewDb();
        var page = await NewQuery(db).ExecuteAsync(username: "alice", canViewAll: true);

        Assert.Equal(3, page.TotalCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNotViewAll_HidesOwnerlessProjects()
    {
        await SeedProjectsAsync();

        await using var db = NewDb();
        // bob chưa tạo project nào nên không thấy gì — kể cả project "không có chủ" (legacy).
        var page = await NewQuery(db).ExecuteAsync(username: "bob", canViewAll: false);

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleOrgUnitCodes_ReturnsProjectsMatchingAny()
    {
        await SeedOrgUnitProjectsAsync();

        await using var db = NewDb();
        // Chọn nhiều đơn vị ⇒ dự án khớp BẤT KỲ mã nào trong danh sách (multi select).
        var page = await NewQuery(db).ExecuteAsync(
            username: "alice", canViewAll: true, orgUnitCodes: new[] { "10", "20" });

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { "10", "20" }, page.SelectedOrgUnitCodesOrEmpty.OrderBy(c => c));
        Assert.All(page.Items, i => Assert.Contains(i.Project.OrgUnitCode, new[] { "10", "20" }));
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyOrgUnitCodes_ReturnsAllProjects()
    {
        await SeedOrgUnitProjectsAsync();

        await using var db = NewDb();
        // Danh sách rỗng (kể cả toàn chuỗi trắng/trùng) ⇒ không lọc theo đơn vị.
        var page = await NewQuery(db).ExecuteAsync(
            username: "alice", canViewAll: true, orgUnitCodes: new[] { "", "  " });

        Assert.Equal(3, page.TotalCount);
        Assert.Empty(page.SelectedOrgUnitCodesOrEmpty);
    }

    private async Task SeedProjectsAsync()
    {
        await using var db = NewDb();
        db.Projects.Add(new Project { Name = "Alice's project", CreatedByUsername = "alice" });
        db.Projects.Add(new Project { Name = "Carol's project", CreatedByUsername = "carol" });
        db.Projects.Add(new Project { Name = "Legacy project", CreatedByUsername = null });
        await db.SaveChangesAsync();
    }

    private async Task SeedOrgUnitProjectsAsync()
    {
        await using var db = NewDb();
        db.Projects.Add(new Project { Name = "P10", CreatedByUsername = "alice", OrgUnitCode = "10" });
        db.Projects.Add(new Project { Name = "P20", CreatedByUsername = "alice", OrgUnitCode = "20" });
        db.Projects.Add(new Project { Name = "P30", CreatedByUsername = "alice", OrgUnitCode = "30" });
        await db.SaveChangesAsync();
    }

    private static GetProjectListQuery NewQuery(AppDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentWorkspace:RootPath"] = Path.Combine(Path.GetTempPath(), "ico-tests")
            })
            .Build();
        return new GetProjectListQuery(db, new WorkspacePathResolver(configuration), new UserDisplayNameResolver(db));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // The ApiKey value-converter needs an IApiKeyProtector; encryption is irrelevant to these tests.
    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
