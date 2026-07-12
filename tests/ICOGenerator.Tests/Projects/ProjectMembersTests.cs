using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Chia sẻ project: chủ project (hoặc người có ProjectsViewAll) mời người dùng khác làm thành viên;
// thành viên thấy project trong danh sách của mình dù không phải người tạo. Username phải là tài
// khoản đang hoạt động; không thêm trùng, không thêm chính chủ project.
public class ProjectMembersTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ProjectMembersTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task AddMember_ByOwner_AddsActiveUser()
    {
        var projectId = await SeedProjectAsync(owner: "alice");
        await SeedUserAsync("bob");

        await using (var db = NewDb())
        {
            var result = await new AddProjectMemberUseCase(db)
                .ExecuteAsync(projectId, " bob ", actorUsername: "alice", canManageAll: false);
            Assert.Equal(AddProjectMemberResult.Added, result);
        }

        await using (var db = NewDb())
        {
            var member = await db.ProjectMembers.SingleAsync();
            Assert.Equal("bob", member.Username);
            Assert.Equal("alice", member.AddedByUsername);
        }
    }

    [Fact]
    public async Task AddMember_ByStranger_IsNotAllowed_ButViewAllBypasses()
    {
        var projectId = await SeedProjectAsync(owner: "alice");
        await SeedUserAsync("bob");

        await using (var db = NewDb())
        {
            // "carol" không phải chủ và không có quyền xem-tất-cả → chặn.
            var denied = await new AddProjectMemberUseCase(db)
                .ExecuteAsync(projectId, "bob", actorUsername: "carol", canManageAll: false);
            Assert.Equal(AddProjectMemberResult.NotAllowed, denied);

            // TeamDev/Admin (canManageAll) thêm hộ được.
            var allowed = await new AddProjectMemberUseCase(db)
                .ExecuteAsync(projectId, "bob", actorUsername: "carol", canManageAll: true);
            Assert.Equal(AddProjectMemberResult.Added, allowed);
        }
    }

    [Fact]
    public async Task AddMember_RejectsUnknownInactiveDuplicateAndOwner()
    {
        var projectId = await SeedProjectAsync(owner: "alice");
        await SeedUserAsync("alice");
        await SeedUserAsync("bob");
        await SeedUserAsync("locked", isActive: false);

        await using var db = NewDb();
        var useCase = new AddProjectMemberUseCase(db);

        Assert.Equal(AddProjectMemberResult.UserNotFound, await useCase.ExecuteAsync(projectId, "ghost", "alice", false));
        Assert.Equal(AddProjectMemberResult.UserNotFound, await useCase.ExecuteAsync(projectId, "locked", "alice", false));
        Assert.Equal(AddProjectMemberResult.UserNotFound, await useCase.ExecuteAsync(projectId, "  ", "alice", false));
        Assert.Equal(AddProjectMemberResult.IsOwner, await useCase.ExecuteAsync(projectId, "alice", "alice", false));

        Assert.Equal(AddProjectMemberResult.Added, await useCase.ExecuteAsync(projectId, "bob", "alice", false));
        Assert.Equal(AddProjectMemberResult.AlreadyMember, await useCase.ExecuteAsync(projectId, "bob", "alice", false));
    }

    [Fact]
    public async Task RemoveMember_OnlyOwnerOrViewAll()
    {
        var projectId = await SeedProjectAsync(owner: "alice");
        await SeedUserAsync("bob");

        Guid memberId;
        await using (var db = NewDb())
        {
            await new AddProjectMemberUseCase(db).ExecuteAsync(projectId, "bob", "alice", false);
            memberId = (await db.ProjectMembers.SingleAsync()).Id;
        }

        await using (var db = NewDb())
        {
            var denied = await new RemoveProjectMemberUseCase(db).ExecuteAsync(memberId, "bob", canManageAll: false);
            Assert.Equal(RemoveProjectMemberResult.NotAllowed, denied);

            var removed = await new RemoveProjectMemberUseCase(db).ExecuteAsync(memberId, "alice", canManageAll: false);
            Assert.Equal(RemoveProjectMemberResult.Removed, removed);

            var notFound = await new RemoveProjectMemberUseCase(db).ExecuteAsync(memberId, "alice", canManageAll: false);
            Assert.Equal(RemoveProjectMemberResult.NotFound, notFound);
        }
    }

    [Fact]
    public async Task ProjectList_ShowsSharedProject_ToMember()
    {
        var projectId = await SeedProjectAsync(owner: "alice", name: "Alice's project");
        await SeedProjectAsync(owner: "carol", name: "Carol's project");
        await SeedUserAsync("bob");

        await using (var db = NewDb())
            await new AddProjectMemberUseCase(db).ExecuteAsync(projectId, "bob", "alice", false);

        await using (var readDb = NewDb())
        {
            // bob không tạo project nào nhưng được share "Alice's project" → thấy đúng một project.
            var page = await NewListQuery(readDb).ExecuteAsync(username: "bob", canViewAll: false);
            Assert.Equal(1, page.TotalCount);
            Assert.Equal("Alice's project", Assert.Single(page.Items).Project.Name);
        }
    }

    [Fact]
    public async Task MembersQuery_ReportsCanManage_ForOwnerAndViewAllOnly()
    {
        var projectId = await SeedProjectAsync(owner: "alice");
        await SeedUserAsync("bob", displayName: "Bob Builder");

        await using (var db = NewDb())
            await new AddProjectMemberUseCase(db).ExecuteAsync(projectId, "bob", "alice", false);

        await using var readDb = NewDb();
        var query = new GetProjectMembersQuery(readDb);

        var asOwner = await query.ExecuteAsync(projectId, "alice", canManageAll: false);
        Assert.NotNull(asOwner);
        Assert.True(asOwner!.CanManage);
        Assert.Equal("Bob Builder", Assert.Single(asOwner.Members).DisplayName);

        var asMember = await query.ExecuteAsync(projectId, "bob", canManageAll: false);
        Assert.False(asMember!.CanManage);

        var asAdmin = await query.ExecuteAsync(projectId, "someone-else", canManageAll: true);
        Assert.True(asAdmin!.CanManage);
    }

    private async Task<Guid> SeedProjectAsync(string owner, string name = "P")
    {
        await using var db = NewDb();
        var project = new Project { Name = name, CreatedByUsername = owner };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task SeedUserAsync(string username, bool isActive = true, string? displayName = null)
    {
        await using var db = NewDb();
        db.AppUsers.Add(new AppUser
        {
            Username = username,
            DisplayName = displayName ?? username,
            PasswordHash = "x",
            Role = UserRole.User,
            IsActive = isActive
        });
        await db.SaveChangesAsync();
    }

    private static GetProjectListQuery NewListQuery(AppDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentWorkspace:RootPath"] = Path.Combine(Path.GetTempPath(), "ico-tests")
            })
            .Build();
        return new GetProjectListQuery(db, new WorkspacePathResolver(configuration));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
