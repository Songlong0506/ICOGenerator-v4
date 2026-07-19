using System.Security.Claims;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Rào chắn truy cập THEO PROJECT: người không có ProjectsViewAll chỉ được đụng project mình tạo —
// kể cả khi đi vòng qua id tài nguyên con (document/revision/source file/call log). "Không phải của
// bạn" và "không tồn tại" phải trả về giống nhau (false) để không rò rỉ sự tồn tại của project.
public class ProjectAccessGuardTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    private readonly Guid _aliceProjectId = Guid.NewGuid();
    private readonly Guid _bobProjectId = Guid.NewGuid();
    private readonly Guid _orphanProjectId = Guid.NewGuid(); // CreatedByUsername null (project cũ)
    private Guid _aliceDocumentId;
    private Guid _aliceRevisionId;
    private Guid _aliceSourceFileId;
    private Guid _aliceCallLogId;

    public ProjectAccessGuardTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();

        // TeamDev có ProjectsViewAll (như seed mặc định); User thường thì không.
        db.RolePermissions.Add(new RolePermission { Role = UserRole.TeamDev, Permission = AppPermission.ProjectsViewAll });

        db.Projects.AddRange(
            new Project { Id = _aliceProjectId, Name = "PA", CreatedByUsername = "alice" },
            new Project { Id = _bobProjectId, Name = "PB", CreatedByUsername = "bob" },
            new Project { Id = _orphanProjectId, Name = "PO", CreatedByUsername = null });

        var model = new AiModel { ModelId = "m", Endpoint = "http://localhost", ApiKey = "" };
        db.AiModels.Add(model);
        var agent = new Agent { AiModelId = model.Id };
        db.Agents.Add(agent);

        var document = new ProjectDocument { ProjectId = _aliceProjectId, FileName = "ProductBrief.docx" };
        _aliceDocumentId = document.Id;
        db.ProjectDocuments.Add(document);

        var revision = new ProjectDocumentRevision { ProjectDocumentId = document.Id, RevisionNumber = 1 };
        _aliceRevisionId = revision.Id;
        db.ProjectDocumentRevisions.Add(revision);

        var source = new ProjectSourceFile { ProjectId = _aliceProjectId, FileName = "spec.pdf" };
        _aliceSourceFileId = source.Id;
        db.ProjectSourceFiles.Add(source);

        var callLog = new AgentModelCallLog { ProjectId = _aliceProjectId, AgentId = agent.Id };
        _aliceCallLogId = callLog.Id;
        db.AgentModelCallLogs.Add(callLog);

        db.SaveChanges();
    }

    [Fact]
    public async Task Owner_CanAccessOwnProject_ButNotOthers()
    {
        var guard = NewGuard();
        var alice = Principal("alice", UserRole.User);

        Assert.True(await guard.CanAccessProjectAsync(alice, _aliceProjectId));
        Assert.False(await guard.CanAccessProjectAsync(alice, _bobProjectId));
    }

    [Fact]
    public async Task ViewAllPermission_GrantsAccessToAnyProject()
    {
        var guard = NewGuard();
        var teamDev = Principal("teamdev", UserRole.TeamDev);

        Assert.True(await guard.CanAccessProjectAsync(teamDev, _aliceProjectId));
        Assert.True(await guard.CanAccessProjectAsync(teamDev, _bobProjectId));
        // Kể cả project không tồn tại: guard chỉ trả lời "có bị chặn không"; tồn tại hay không do action xử lý.
        Assert.True(await guard.CanAccessProjectAsync(teamDev, Guid.NewGuid()));
    }

    [Fact]
    public async Task NonexistentAndOrphanProjects_LookIdenticalToForeignOnes()
    {
        var guard = NewGuard();
        var alice = Principal("alice", UserRole.User);

        Assert.False(await guard.CanAccessProjectAsync(alice, Guid.NewGuid()));
        // Project "không có chủ" (tạo trước khi có CreatedByUsername) cũng ẩn với user thường —
        // khớp hành vi lọc ở danh sách Projects.
        Assert.False(await guard.CanAccessProjectAsync(alice, _orphanProjectId));
    }

    [Fact]
    public async Task Unauthenticated_OrMissingName_IsAlwaysDenied()
    {
        var guard = NewGuard();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(await guard.CanAccessProjectAsync(anonymous, _aliceProjectId));
    }

    [Fact]
    public async Task ChildResources_ResolveBackToOwningProject()
    {
        var guard = NewGuard();
        var alice = Principal("alice", UserRole.User);
        var bob = Principal("bob", UserRole.User);

        Assert.True(await guard.CanAccessDocumentAsync(alice, _aliceDocumentId));
        Assert.True(await guard.CanAccessDocumentRevisionAsync(alice, _aliceRevisionId));
        Assert.True(await guard.CanAccessSourceFileAsync(alice, _aliceSourceFileId));
        Assert.True(await guard.CanAccessCallLogAsync(alice, _aliceCallLogId));

        // Cùng các id đó nhưng là người khác → chặn (đây chính là lỗ IDOR trước đây).
        Assert.False(await guard.CanAccessDocumentAsync(bob, _aliceDocumentId));
        Assert.False(await guard.CanAccessDocumentRevisionAsync(bob, _aliceRevisionId));
        Assert.False(await guard.CanAccessSourceFileAsync(bob, _aliceSourceFileId));
        Assert.False(await guard.CanAccessCallLogAsync(bob, _aliceCallLogId));

        // Id không tồn tại → false, không phân biệt được với "không phải của bạn".
        Assert.False(await guard.CanAccessDocumentAsync(alice, Guid.NewGuid()));
    }

    private ProjectAccessGuard NewGuard()
    {
        var db = NewDb();
        return new ProjectAccessGuard(db, new PermissionService(db, new MemoryCache(new MemoryCacheOptions())));
    }

    private static ClaimsPrincipal Principal(string username, UserRole role) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role.ToString()) },
            authenticationType: "test"));

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
