using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements.Knowledge;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Tri thức xuyên dự án: khối ngữ cảnh chỉ được trích từ tài liệu ĐÃ DUYỆT của dự án KHÁC, ưu tiên dự án
// cùng đơn vị yêu cầu, dùng chỉ mục cache theo tiến trình, và fail-open ở mọi nhánh lỗi.
public class ProjectKnowledgeServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _currentProjectId = Guid.NewGuid();

    public ProjectKnowledgeServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _currentProjectId, Name = "Kho mới", Description = "" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Build_ReturnsExcerptFromOtherProjectsApprovedDoc_WithSourceProjectName()
    {
        await SeedDocAsync("Dự án kho cũ", "# Phạm vi\nỨng dụng quản lý kho vật tư giúp thủ kho theo dõi nhập xuất tồn hằng ngày tại phân xưởng.", approved: true);

        var result = await NewSut().BuildKnowledgeContextAsync(
            _currentProjectId, "Kho mới", "", null, "ứng dụng quản lý kho vật tư");

        Assert.NotNull(result);
        Assert.Contains("Dự án \"Dự án kho cũ\"", result);
        Assert.Contains("quản lý kho vật tư", result);
        Assert.Contains("Product Brief", result);
    }

    [Fact]
    public async Task Build_IgnoresUnapprovedDocs_AndOwnProjectDocs()
    {
        await SeedDocAsync("Dự án nháp", "# Phạm vi\nUNAPPROVED-MARKER ứng dụng quản lý kho vật tư nhập xuất tồn của phân xưởng.", approved: false);
        await SeedDocAsync("Kho mới", "# Phạm vi\nSELF-MARKER ứng dụng quản lý kho vật tư nhập xuất tồn của phân xưởng.", approved: true,
            projectId: _currentProjectId);

        var result = await NewSut().BuildKnowledgeContextAsync(
            _currentProjectId, "Kho mới", "", null, "ứng dụng quản lý kho vật tư");

        // Không còn nguồn hợp lệ nào ⇒ không có khối tri thức (chứ không rò bản nháp / tài liệu của chính mình).
        Assert.Null(result);
    }

    [Fact]
    public async Task Build_NoApprovedDocsAnywhere_ReturnsNull()
    {
        var result = await NewSut().BuildKnowledgeContextAsync(
            _currentProjectId, "Kho mới", "", null, "ứng dụng quản lý kho vật tư");

        Assert.Null(result);
    }

    [Fact]
    public async Task Build_SameOrgUnitProject_RanksFirstWhenEquallyRelevant()
    {
        const string content = "# Phạm vi\nỨng dụng quản lý kho vật tư giúp thủ kho theo dõi nhập xuất tồn hằng ngày tại phân xưởng.";
        await SeedDocAsync("Dự án khác phòng", content, approved: true, orgUnitCode: "HcP/TEF");
        await SeedDocAsync("Dự án cùng phòng", content, approved: true, orgUnitCode: "HcP/MFW");

        var result = await NewSut().BuildKnowledgeContextAsync(
            _currentProjectId, "Kho mới", "", "HcP/MFW", "ứng dụng quản lý kho vật tư");

        Assert.NotNull(result);
        var samePos = result!.IndexOf("Dự án cùng phòng", StringComparison.Ordinal);
        var otherPos = result.IndexOf("Dự án khác phòng", StringComparison.Ordinal);
        Assert.True(samePos >= 0);
        // Cùng nội dung ⇒ boost cùng-đơn-vị phải đưa dự án cùng phòng lên trước.
        Assert.True(otherPos < 0 || samePos < otherPos);
    }

    [Fact]
    public async Task Build_IndexIsCached_DocApprovedLaterNotVisibleUntilExpiry()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = NewSut(cache);

        // Lần gọi đầu dựng chỉ mục khi corpus còn trống.
        Assert.Null(await sut.BuildKnowledgeContextAsync(
            _currentProjectId, "Kho mới", "", null, "ứng dụng quản lý kho vật tư"));

        await SeedDocAsync("Dự án mới duyệt", "# Phạm vi\nỨng dụng quản lý kho vật tư nhập xuất tồn hằng ngày của phân xưởng.", approved: true);

        // Chỉ mục đã cache (10 phút) ⇒ tài liệu vừa duyệt CHƯA xuất hiện — đúng thiết kế đánh đổi độ tươi lấy chi phí.
        Assert.Null(await sut.BuildKnowledgeContextAsync(
            _currentProjectId, "Kho mới", "", null, "ứng dụng quản lý kho vật tư"));
    }

    [Fact]
    public async Task Build_TemplateLoadFails_FailsOpenToNull()
    {
        await SeedDocAsync("Dự án kho cũ", "# Phạm vi\nỨng dụng quản lý kho vật tư nhập xuất tồn hằng ngày của phân xưởng.", approved: true);

        await using var db = NewDb();
        var sut = new ProjectKnowledgeService(db, new ThrowingPrompts(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ProjectKnowledgeService>.Instance);

        Assert.Null(await sut.BuildKnowledgeContextAsync(
            _currentProjectId, "Kho mới", "", null, "ứng dụng quản lý kho vật tư"));
    }

    private ProjectKnowledgeService NewSut(IMemoryCache? cache = null) =>
        new(NewDb(), new StubPrompts(), cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ProjectKnowledgeService>.Instance);

    private async Task SeedDocAsync(string projectName, string content, bool approved,
        string? orgUnitCode = null, Guid? projectId = null)
    {
        await using var db = NewDb();
        var id = projectId ?? Guid.NewGuid();
        if (projectId == null)
            db.Projects.Add(new Project { Id = id, Name = projectName, Description = "", OrgUnitCode = orgUnitCode });

        db.ProjectDocuments.Add(new ProjectDocument
        {
            ProjectId = id,
            FileName = "ProductBrief.docx",
            VersionName = "V1",
            IsApproved = approved,
            Content = content
        });
        await db.SaveChangesAsync();
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "<!-- ghi chú -->\n## Tri thức từ các dự án trước\n\n{{EXCERPTS}}";
    }

    private sealed class ThrowingPrompts : PromptTemplateService
    {
        public ThrowingPrompts() : base(null!) { }
        public override string Get(string relativePath) => throw new FileNotFoundException(relativePath);
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
