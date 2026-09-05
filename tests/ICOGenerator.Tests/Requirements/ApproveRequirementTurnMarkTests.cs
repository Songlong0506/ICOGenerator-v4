using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Approve đóng MỐC DUYỆT trên hội thoại (Project.BriefApprovedTurnCount). Mốc này là điều kiện để vòng
// soạn Brief sau đó được phép nén phần transcript trước mốc: bản vừa duyệt là bản duy nhất có chữ ký
// người dùng, nên nó chở lại đúng phần bị cắt. Không đóng mốc ⇒ cửa sổ nén mất một trong ba nguồn cắt.
public class ApproveRequirementTurnMarkTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public ApproveRequirementTurnMarkTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "m" };
        var ba = new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id };
        db.AiModels.Add(model);
        db.Agents.Add(ba);
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.ProjectDocuments.Add(new ProjectDocument
        {
            ProjectId = _projectId,
            VersionName = "draft",
            IsApproved = false,
            Folder = "01_Requirement",
            FileName = "ProductBrief.docx",
            Content = "bản nháp"
        });
        for (var i = 0; i < 7; i++)
        {
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = ba.Id,
                Role = i % 2 == 0 ? "user" : "assistant",
                Message = $"lượt {i}",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, i, DateTimeKind.Utc)
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_StampsConversationTurnCount_AtApprovalTime()
    {
        await using var db = NewDb();

        var result = await NewSut(db).ExecuteAsync(_projectId);

        Assert.Equal(ApproveRequirementResult.Approved, result);

        await using var verify = NewDb();
        var project = await verify.Projects.SingleAsync(p => p.Id == _projectId);
        // Đếm MỌI lượt (không lọc lượt BA/lượt rỗng) để khớp con trỏ của các tầng bộ nhớ khác.
        Assert.Equal(7, project.BriefApprovedTurnCount);
    }

    // Approve cũng MỞ HÀNG ĐỢI học: đây là mốc đầu tiên có đủ hai vế (hội thoại + ghi chú người dùng ghim
    // lên chính bản vừa duyệt) để hỏi "buổi phỏng vấn thiếu câu nào". Cổng chỉ ghi tên version (vài
    // UPDATE) — chắt lọc là một lời gọi LLM, do RequirementMemoryHarvester chạy nền ở task kế.
    [Fact]
    public async Task ExecuteAsync_QueuesChecklistHarvest_ForTheVersionJustApproved()
    {
        await using var db = NewDb();

        Assert.Equal(ApproveRequirementResult.Approved, await NewSut(db).ExecuteAsync(_projectId));

        await using var verify = NewDb();
        var project = await verify.Projects.SingleAsync(p => p.Id == _projectId);
        // Đúng tên bản vừa duyệt — cũng là dấu mà ghi chú Brief của bản đó vừa được đóng lên.
        Assert.Equal("V1", project.PendingChecklistHarvestVersion);
    }

    private ApproveRequirementUseCase NewSut(AppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentWorkspace:RootPath"] = Path.Combine(Path.GetTempPath(), "ico-tests", Guid.NewGuid().ToString("N"))
            })
            .Build();

        return new ApproveRequirementUseCase(
            db,
            new WorkspacePathResolver(config),
            new ProjectArtifactCatalog(),
            new FakeOrchestrator(),
            NullLogger<ApproveRequirementUseCase>.Instance);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeOrchestrator : IWorkflowOrchestrator
    {
        public Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId, bool coalesceWithActiveRun = false) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> StartDeliveryWorkflowAsync(Guid projectId, string v, string s) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> StartAiDesignSpecWorkflowAsync(Guid projectId, string v) => Task.FromResult(Guid.NewGuid());
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
