using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Chặng của dự án được SUY RA từ dữ liệu đã có, không có cột nào lưu sẵn (xem ProjectStatusResolver).
// Bộ test này khoá hai thứ: (1) thứ tự xét là "chặng CAO NHẤT đã đạt" — duyệt Brief rồi soạn lại bản
// nháp mới thì không tụt hạng; (2) lượt chat đã bị "＋ New Chat" lưu trữ vẫn tính là ĐÃ TỪNG chat —
// đây là cái bẫy của global query filter ArchivedAt == null, và nó chỉ lộ ra khi chạy thật xuống DB.
public class ProjectStatusResolverTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _agentId = Guid.NewGuid();

    public ProjectStatusResolverTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "m" };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = _agentId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id });
        db.SaveChanges();
    }

    [Fact]
    public async Task Du_an_chua_chat_gi_la_New()
    {
        var id = await SeedProjectAsync();

        Assert.Equal(ProjectStatus.New, (await ResolveAsync(id)).Status);
    }

    [Fact]
    public async Task Co_luot_chat_la_GetRequirement()
    {
        var id = await SeedProjectAsync();
        await AddTurnAsync(id);

        Assert.Equal(ProjectStatus.GetRequirement, (await ResolveAsync(id)).Status);
    }

    // "＋ New Chat" chỉ đóng dấu ArchivedAt chứ không xoá lượt chat, và mọi đường đọc thường lọc
    // ArchivedAt == null. Nếu phép đếm ở đây cũng dính filter đó thì một dự án đã phỏng vấn cả buổi rồi
    // bấm New Chat sẽ rơi ngược về New — badge vẫn hiện, chỉ là sai.
    [Fact]
    public async Task Luot_chat_da_luu_tru_van_tinh_la_da_tung_chat()
    {
        var id = await SeedProjectAsync();
        await AddTurnAsync(id, archived: true);

        Assert.Equal(ProjectStatus.GetRequirement, (await ResolveAsync(id)).Status);
    }

    [Fact]
    public async Task Co_ban_nhap_Product_Brief_la_ProductBriefDraft()
    {
        var id = await SeedProjectAsync();
        await AddTurnAsync(id);
        await AddBriefAsync(id, approved: false);

        var row = await ResolveAsync(id);
        Assert.Equal(ProjectStatus.ProductBriefDraft, row.Status);
        // Cờ chỉ nói "đã duyệt rồi mà lại có bản nháp mới" — bản nháp đầu tiên chưa phải ca đó.
        Assert.False(row.HasPendingBriefDraft);
    }

    // Tài liệu khác Product Brief (AI Design Spec sinh sau khi duyệt, technical docs…) không phải cái
    // quyết định chặng — nếu không, mọi dự án có spec sẽ đứng nhầm chỗ.
    [Fact]
    public async Task Tai_lieu_khac_Product_Brief_khong_doi_chang()
    {
        var id = await SeedProjectAsync();
        await AddTurnAsync(id);
        await AddDocumentAsync(id, "AIDesignSpec.docx", approved: false);

        Assert.Equal(ProjectStatus.GetRequirement, (await ResolveAsync(id)).Status);
    }

    [Fact]
    public async Task Brief_da_duyet_la_ProductBriefApproved()
    {
        var id = await SeedProjectAsync();
        await AddTurnAsync(id);
        await AddBriefAsync(id, approved: true);

        var row = await ResolveAsync(id);
        Assert.Equal(ProjectStatus.ProductBriefApproved, row.Status);
        Assert.False(row.HasPendingBriefDraft);
    }

    // Vòng soạn lại Brief từ ghi chú POC / góp ý trên Brief đẻ ra một bản nháp mới bên cạnh bản V{n} đã
    // duyệt. Chặng KHÔNG tụt về Draft (badge sẽ nhảy tới nhảy lui theo mỗi vòng góp ý), chỉ bật cờ.
    [Fact]
    public async Task Duyet_roi_soan_lai_ban_nhap_van_la_Approved_kem_co_cho_duyet()
    {
        var id = await SeedProjectAsync();
        await AddTurnAsync(id);
        await AddBriefAsync(id, approved: true);
        await AddBriefAsync(id, approved: false);

        var row = await ResolveAsync(id);
        Assert.Equal(ProjectStatus.ProductBriefApproved, row.Status);
        Assert.True(row.HasPendingBriefDraft);
    }

    [Fact]
    public async Task Nghiem_thu_POC_la_chang_cao_nhat()
    {
        var id = await SeedProjectAsync();
        await AddTurnAsync(id);
        await AddBriefAsync(id, approved: true);
        await AddBriefAsync(id, approved: false);

        await using (var db = NewDb())
        {
            var project = await db.Projects.SingleAsync(p => p.Id == id);
            project.PocAcceptedAtUtc = DateTime.UtcNow;
            project.PocAcceptedBy = "lan.nguyen";
            await db.SaveChangesAsync();
        }

        Assert.Equal(ProjectStatus.PocApproved, (await ResolveAsync(id)).Status);
    }

    // Rút nghiệm thu trả dự án về đúng chặng trước đó — công tắc hai chiều, không để lại chặng "mồ côi".
    [Fact]
    public async Task Rut_nghiem_thu_ve_lai_ProductBriefApproved()
    {
        var id = await SeedProjectAsync();
        await AddTurnAsync(id);
        await AddBriefAsync(id, approved: true);

        await using (var db = NewDb())
        {
            var project = await db.Projects.SingleAsync(p => p.Id == id);
            project.PocAcceptedAtUtc = DateTime.UtcNow;
            project.PocAcceptedBy = "lan.nguyen";
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var project = await db.Projects.SingleAsync(p => p.Id == id);
            project.PocAcceptedAtUtc = null;
            project.PocAcceptedBy = null;
            await db.SaveChangesAsync();
        }

        Assert.Equal(ProjectStatus.ProductBriefApproved, (await ResolveAsync(id)).Status);
    }

    [Fact]
    public async Task ResolveMany_tra_ve_dung_chang_cua_tung_du_an()
    {
        var moi = await SeedProjectAsync();
        var dangChat = await SeedProjectAsync();
        await AddTurnAsync(dangChat);
        var daDuyet = await SeedProjectAsync();
        await AddBriefAsync(daDuyet, approved: true);

        await using var db = NewDb();
        var map = await NewResolver(db).ResolveManyAsync(new[] { moi, dangChat, daDuyet });

        Assert.Equal(3, map.Count);
        Assert.Equal(ProjectStatus.New, map[moi].Status);
        Assert.Equal(ProjectStatus.GetRequirement, map[dangChat].Status);
        Assert.Equal(ProjectStatus.ProductBriefApproved, map[daDuyet].Status);
    }

    [Fact]
    public async Task ResolveMany_khong_co_id_nao_thi_khong_cham_DB()
    {
        await using var db = NewDb();

        Assert.Empty(await NewResolver(db).ResolveManyAsync(Array.Empty<Guid>()));
    }

    [Fact]
    public async Task Resolve_du_an_khong_ton_tai_tra_null()
    {
        await using var db = NewDb();

        Assert.Null(await NewResolver(db).ResolveAsync(Guid.NewGuid()));
    }

    private async Task<ProjectStatusRow> ResolveAsync(Guid projectId)
    {
        await using var db = NewDb();
        var row = await NewResolver(db).ResolveAsync(projectId);
        return Assert.IsType<ProjectStatusRow>(row);
    }

    private static ProjectStatusResolver NewResolver(AppDbContext db) => new(db, new ProjectArtifactCatalog());

    private async Task<Guid> SeedProjectAsync()
    {
        await using var db = NewDb();
        var project = new Project { Name = "P" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task AddTurnAsync(Guid projectId, bool archived = false)
    {
        await using var db = NewDb();
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = _agentId,
            Role = "user",
            Message = "chào BA",
            ArchivedAt = archived ? DateTime.UtcNow : null
        });
        await db.SaveChangesAsync();
    }

    private Task AddBriefAsync(Guid projectId, bool approved) =>
        AddDocumentAsync(projectId, "ProductBrief.docx", approved);

    private async Task AddDocumentAsync(Guid projectId, string fileName, bool approved)
    {
        await using var db = NewDb();
        db.ProjectDocuments.Add(new ProjectDocument
        {
            ProjectId = projectId,
            Folder = "01_Requirement",
            FileName = fileName,
            VersionName = approved ? "V1" : "draft",
            IsApproved = approved,
            Content = "nội dung"
        });
        await db.SaveChangesAsync();
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
