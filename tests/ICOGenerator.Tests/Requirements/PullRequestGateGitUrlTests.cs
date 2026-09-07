using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng duyệt trước bước Pull Request đòi Git URL của MỌI repo đích — và "mọi repo đích" là bao nhiêu thì
// phụ thuộc Generation Mode: khung Bosch tách backend/frontend thành hai repo, không dùng khung Bosch thì
// toàn bộ code nằm trong một cây duy nhất nên chỉ có một repo (xem ProjectRepositoryLayout).
public class PullRequestGateGitUrlTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PullRequestGateGitUrlTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task BoschTemplate_WithOnlyBackendUrl_IsBlocked()
    {
        var (projectId, runId) = await SeedRunWaitingBeforePullRequestAsync(
            useBoschTemplate: true, backendGitUrl: "https://git.example.com/be.git", frontendGitUrl: null);

        await using var db = NewDb();
        Assert.Equal(ApproveStageResult.MissingGitUrls,
            await new ApproveStageUseCase(db, new ProjectArtifactCatalog()).ExecuteAsync(projectId, runId));
    }

    [Fact]
    public async Task BoschTemplate_WithBothUrls_Advances()
    {
        var (projectId, runId) = await SeedRunWaitingBeforePullRequestAsync(
            useBoschTemplate: true,
            backendGitUrl: "https://git.example.com/be.git",
            frontendGitUrl: "https://git.example.com/fe.git");

        await using var db = NewDb();
        Assert.Equal(ApproveStageResult.Advanced,
            await new ApproveStageUseCase(db, new ProjectArtifactCatalog()).ExecuteAsync(projectId, runId));
    }

    // Dự án KHÔNG dùng khung Bosch chỉ có một repo đích, nên Frontend Git không có đường nào đọc tới.
    // Trước đây cổng vẫn đòi cả hai — người vận hành buộc phải điền một URL giả để đi tiếp.
    [Fact]
    public async Task WithoutBoschTemplate_BackendUrlAlone_IsEnough()
    {
        var (projectId, runId) = await SeedRunWaitingBeforePullRequestAsync(
            useBoschTemplate: false, backendGitUrl: "https://git.example.com/app.git", frontendGitUrl: null);

        await using var db = NewDb();
        Assert.Equal(ApproveStageResult.Advanced,
            await new ApproveStageUseCase(db, new ProjectArtifactCatalog()).ExecuteAsync(projectId, runId));
    }

    // Không repo nào có URL thì vẫn chặn: bước Pull Request không có chỗ nào để đẩy nhánh lên.
    [Fact]
    public async Task WithoutBoschTemplate_WithNoUrlAtAll_IsBlocked()
    {
        var (projectId, runId) = await SeedRunWaitingBeforePullRequestAsync(
            useBoschTemplate: false, backendGitUrl: null, frontendGitUrl: null);

        await using var db = NewDb();
        Assert.Equal(ApproveStageResult.MissingGitUrls,
            await new ApproveStageUseCase(db, new ProjectArtifactCatalog()).ExecuteAsync(projectId, runId));
    }

    // Cổng chỉ soát Git URL ở ĐÚNG bước tiêu thụ nó: các cổng trước đó không được chặn vì lý do này.
    [Fact]
    public async Task EarlierGate_DoesNotRequireGitUrls()
    {
        var (projectId, runId) = await SeedRunAsync(
            WorkflowStageKey.CodeReview, AgentTaskType.CodeReview,
            useBoschTemplate: true, backendGitUrl: null, frontendGitUrl: null);

        await using var db = NewDb();
        Assert.Equal(ApproveStageResult.Advanced,
            await new ApproveStageUseCase(db, new ProjectArtifactCatalog()).ExecuteAsync(projectId, runId));
    }

    // Run đang chờ duyệt ở bước Testing ⇒ bước kế là Pull Request, tức cổng cần soát Git URL.
    private Task<(Guid ProjectId, Guid RunId)> SeedRunWaitingBeforePullRequestAsync(
        bool useBoschTemplate, string? backendGitUrl, string? frontendGitUrl) =>
        SeedRunAsync(WorkflowStageKey.Testing, AgentTaskType.Testing, useBoschTemplate, backendGitUrl, frontendGitUrl);

    private async Task<(Guid ProjectId, Guid RunId)> SeedRunAsync(
        WorkflowStageKey stage, AgentTaskType completedTaskType,
        bool useBoschTemplate, string? backendGitUrl, string? frontendGitUrl)
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using var db = NewDb();

        // Bước kế (Pull Request / Testing) cần sẵn agent của vai tương ứng, nếu không use case dừng ở
        // MissingAgent trước khi tới thứ test này muốn đo.
        var model = new AiModel { ModelId = "m", Endpoint = "http://localhost", ApiKey = "" };
        db.AiModels.Add(model);
        foreach (var role in new[] { AgentRoleKey.Developer, AgentRoleKey.Tester })
            db.Agents.Add(new Agent { RoleKey = role, AiModelId = model.Id });

        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "P",
            IsUseBoschTemplate = useBoschTemplate,
            BackendGitUrl = backendGitUrl,
            FrontendGitUrl = frontendGitUrl
        });
        db.WorkflowRuns.Add(new WorkflowRun
        {
            Id = runId,
            ProjectId = projectId,
            Status = WorkflowRunStatus.WaitingForHuman,
            CurrentStage = stage
        });
        db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = runId,
            ProjectId = projectId,
            Type = completedTaskType,
            Status = AgentTaskStatus.Completed,
            Title = stage.ToString(),
            Output = "bàn giao",
            FinishedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return (projectId, runId);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();
}
