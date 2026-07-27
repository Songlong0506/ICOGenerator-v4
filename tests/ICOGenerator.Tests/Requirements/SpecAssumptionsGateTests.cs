using ICOGenerator.Application.Requirements;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// CỔNG XÁC NHẬN GIẢ ĐỊNH giữa "sinh AI Design Spec" và "dựng POC". Chốt các bất biến:
//  (1) Xác nhận ⇒ gỡ cổng RỒI mới khởi động delivery (đúng lời gọi worker vẫn chạy trước đây).
//  (2) Bấm lần hai (hai tab) không tạo ra hai delivery run trên cùng workspace.
//  (3) Báo sai ⇒ đính chính đi vào CẢ hội thoại LẪN cột nạp thẳng vào prompt sinh spec — chỉ ghi hội
//      thoại thì lượt sinh lại vẫn đẻ ra đúng giả định vừa bị bác (spec sinh từ Brief, không đọc transcript).
public class SpecAssumptionsGateTests : IDisposable
{
    private const string SpecFileName = "AiDesignSpec.docx";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public SpecAssumptionsGateTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "m" };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", PendingAssumptionsVersion = "V1" });
        db.ProjectDocuments.Add(new ProjectDocument
        {
            ProjectId = _projectId,
            VersionName = "V1",
            FileName = SpecFileName,
            Content = "# AI Design Spec\n## 12. Assumptions\n- Mỗi nhân viên chỉ thuộc một phòng ban",
            IsApproved = true
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Confirm_ClearsGate_AndStartsDeliveryWithTheApprovedSpec()
    {
        var orchestrator = new FakeOrchestrator();
        await using var db = NewDb();

        var result = await new ConfirmSpecAssumptionsUseCase(db, new FakeCatalog(), orchestrator)
            .ExecuteAsync(_projectId);

        Assert.Equal(ConfirmAssumptionsResult.Ok, result);
        Assert.Equal(_projectId, orchestrator.DeliveryProjectId);
        Assert.Equal("V1", orchestrator.DeliveryVersion);
        Assert.Contains("Mỗi nhân viên chỉ thuộc một phòng ban", orchestrator.DeliverySpec);

        await using var verify = NewDb();
        Assert.Null((await verify.Projects.SingleAsync()).PendingAssumptionsVersion);
    }

    [Fact]
    public async Task Confirm_Twice_DoesNotStartTwoDeliveryRuns()
    {
        var orchestrator = new FakeOrchestrator();

        await using (var db = NewDb())
            await new ConfirmSpecAssumptionsUseCase(db, new FakeCatalog(), orchestrator).ExecuteAsync(_projectId);

        await using var second = NewDb();
        var result = await new ConfirmSpecAssumptionsUseCase(second, new FakeCatalog(), orchestrator)
            .ExecuteAsync(_projectId);

        Assert.Equal(ConfirmAssumptionsResult.NothingPending, result);
        Assert.Equal(1, orchestrator.DeliveryStarts);
    }

    [Fact]
    public async Task Confirm_WithNoPendingGate_DoesNothing()
    {
        await using (var clear = NewDb())
        {
            (await clear.Projects.SingleAsync()).PendingAssumptionsVersion = null;
            await clear.SaveChangesAsync();
        }

        var orchestrator = new FakeOrchestrator();
        await using var db = NewDb();

        Assert.Equal(ConfirmAssumptionsResult.NothingPending,
            await new ConfirmSpecAssumptionsUseCase(db, new FakeCatalog(), orchestrator).ExecuteAsync(_projectId));
        Assert.Equal(0, orchestrator.DeliveryStarts);
    }

    [Fact]
    public async Task Revise_RecordsCorrections_InConversationAndOnProject_AndRegeneratesSpec()
    {
        var orchestrator = new FakeOrchestrator();
        await using var db = NewDb();

        var result = await NewReviseSut(db, orchestrator).ExecuteAsync(_projectId, new List<AssumptionCorrection>
        {
            new() { Assumption = "Mỗi nhân viên chỉ thuộc một phòng ban", Correction = "Nhân viên có thể kiêm nhiệm 2 phòng" },
            new() { Assumption = "Đơn đã duyệt thì không sửa được" }
        });

        Assert.Equal(ReviseAssumptionsResult.Ok, result);

        // Sinh LẠI spec cho đúng phiên bản đang chờ — không phải dựng POC.
        Assert.Equal(_projectId, orchestrator.SpecProjectId);
        Assert.Equal("V1", orchestrator.SpecVersion);
        Assert.Equal(0, orchestrator.DeliveryStarts);

        await using var verify = NewDb();
        var project = await verify.Projects.SingleAsync();

        // Cổng gỡ để lượt sinh mới dựng lại cổng (không kẹt hai lượt chồng nhau).
        Assert.Null(project.PendingAssumptionsVersion);

        // Đường TẤT ĐỊNH nạp vào prompt sinh spec.
        Assert.Contains("Nhân viên có thể kiêm nhiệm 2 phòng", project.SpecAssumptionCorrections);
        Assert.Contains("Đơn đã duyệt thì không sửa được", project.SpecAssumptionCorrections);

        // Đường HỘI THOẠI (bản đồ bao phủ / decision log / checklist-gap ăn theo transcript).
        var turn = await verify.AgentConversations.SingleAsync();
        Assert.Equal("user", turn.Role);
        Assert.Contains("Nhân viên có thể kiêm nhiệm 2 phòng", turn.Message);
    }

    [Fact]
    public async Task Revise_AccumulatesCorrectionsAcrossRounds()
    {
        var orchestrator = new FakeOrchestrator();

        await using (var db = NewDb())
            await NewReviseSut(db, orchestrator).ExecuteAsync(_projectId, new List<AssumptionCorrection>
            {
                new() { Assumption = "Giả định vòng 1", Correction = "sửa 1" }
            });

        // Vòng sinh spec mới dựng lại cổng.
        await using (var reopen = NewDb())
        {
            (await reopen.Projects.SingleAsync()).PendingAssumptionsVersion = "V1";
            await reopen.SaveChangesAsync();
        }

        await using (var db = NewDb())
            await NewReviseSut(db, orchestrator).ExecuteAsync(_projectId, new List<AssumptionCorrection>
            {
                new() { Assumption = "Giả định vòng 2", Correction = "sửa 2" }
            });

        await using var verify = NewDb();
        var corrections = (await verify.Projects.SingleAsync()).SpecAssumptionCorrections;
        Assert.Contains("sửa 1", corrections);
        Assert.Contains("sửa 2", corrections);
    }

    [Fact]
    public async Task Revise_WithNothingMarked_ReturnsNoNotes_AndLeavesGateOpen()
    {
        var orchestrator = new FakeOrchestrator();
        await using var db = NewDb();

        var result = await NewReviseSut(db, orchestrator).ExecuteAsync(_projectId, new List<AssumptionCorrection>
        {
            new() { Assumption = "   " }
        });

        Assert.Equal(ReviseAssumptionsResult.NoNotes, result);
        Assert.Equal("V1", (await NewDb().Projects.SingleAsync()).PendingAssumptionsVersion);
        Assert.Equal(0, await NewDb().AgentConversations.CountAsync());
    }

    private ReviseSpecAssumptionsUseCase NewReviseSut(AppDbContext db, IWorkflowOrchestrator orchestrator) =>
        new(db, new BAConversationLog(db), new BAAgentResolver(db), orchestrator);

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeCatalog : IProjectArtifactCatalog
    {
        public ProjectArtifactDescriptor ProductBrief { get; } =
            new("productBrief", "ProductBrief.docx", "Product Brief", true, "01_Requirement");
        public ProjectArtifactDescriptor AiDesignSpec { get; } =
            new("aiDesignSpec", SpecFileName, "AI Design Spec", true, "02_Design");
        public IReadOnlyList<ProjectArtifactDescriptor> TechnicalDocuments { get; } = Array.Empty<ProjectArtifactDescriptor>();
    }

    private sealed class FakeOrchestrator : IWorkflowOrchestrator
    {
        public int DeliveryStarts;
        public Guid? DeliveryProjectId;
        public string? DeliveryVersion;
        public string? DeliverySpec;
        public Guid? SpecProjectId;
        public string? SpecVersion;

        public Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId) => Task.FromResult(Guid.NewGuid());

        public Task<Guid> StartDeliveryWorkflowAsync(Guid projectId, string versionName, string spec)
        {
            DeliveryStarts++;
            DeliveryProjectId = projectId;
            DeliveryVersion = versionName;
            DeliverySpec = spec;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<Guid> StartAiDesignSpecWorkflowAsync(Guid projectId, string versionName)
        {
            SpecProjectId = projectId;
            SpecVersion = versionName;
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
