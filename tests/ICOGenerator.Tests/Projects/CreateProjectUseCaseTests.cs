using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Đơn vị yêu cầu khi tạo project: chỉ lưu OrgUnitCode khi mã CÓ THẬT trong OrgUnits (dropdown render từ
// DB nên mã lạ chỉ đến từ request tự chế) — mã lạ/đã xóa mềm bị lặng lẽ bỏ qua, KHÔNG chặn tạo project.
public class CreateProjectUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public CreateProjectUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "50123", DisplayName = "HcP/TEF3.3" });
        db.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "50999", DisplayName = "HcP/GONE", IsDelete = true });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_WithValidOrgUnitCode_StoresTrimmedCode()
    {
        await using var db = NewDb();
        var id = await NewSut(db).ExecuteAsync(new ProjectCreateVm { Name = "P", OrgUnitCode = " 50123 " }, "alice");

        var project = await NewDb().Projects.SingleAsync(p => p.Id == id);
        Assert.Equal("50123", project.OrgUnitCode);
        Assert.Equal("alice", project.CreatedByUsername);
    }

    [Theory]
    [InlineData(null)]        // không chọn đơn vị
    [InlineData("")]          // form gửi chuỗi rỗng
    [InlineData("60000")]     // mã không tồn tại
    [InlineData("50999")]     // mã đã xóa mềm trong dữ liệu HR
    public async Task ExecuteAsync_WithMissingOrUnknownCode_StoresNull_AndStillCreatesProject(string? code)
    {
        await using var db = NewDb();
        var id = await NewSut(db).ExecuteAsync(new ProjectCreateVm { Name = "P", OrgUnitCode = code });

        var project = await NewDb().Projects.SingleAsync(p => p.Id == id);
        Assert.Null(project.OrgUnitCode);
    }

    private static CreateProjectUseCase NewSut(AppDbContext db) =>
        new(db, new FakeArtifactStorage(), NullLogger<CreateProjectUseCase>.Instance);

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeArtifactStorage : IArtifactStorage
    {
        public void InitializeProjectWorkspace(string projectKey) { }
        public string GetDraftPath(string projectKey, ProjectArtifactDescriptor artifact) => Path.Combine(Path.GetTempPath(), artifact.FileName);
        public string GetVersionPath(string projectKey, string versionName, ProjectArtifactDescriptor artifact) => Path.Combine(Path.GetTempPath(), versionName, artifact.FileName);
        public string GetSourceUploadDir(string projectKey) => Path.GetTempPath();
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
