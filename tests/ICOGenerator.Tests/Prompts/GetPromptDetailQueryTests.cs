using ICOGenerator.Application.Prompts;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace ICOGenerator.Tests.Prompts;

// Trang chi tiết template: bảng "điểm eval theo phiên bản" gộp kết quả judge của các scenario thuộc
// template theo PromptVersionNumber (null = file, đứng đầu), bỏ kết quả lỗi; template không có
// scenario thì bảng rỗng.
public class GetPromptDetailQueryTests : IDisposable
{
    private const string Key = "BA/a.md";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public GetPromptDetailQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        // Template mồ côi (không có file) sống bằng phiên bản DB — detail vẫn phải trả về được.
        db.PromptTemplateVersions.Add(new PromptTemplateVersion { PromptKey = Key, VersionNumber = 1, Content = "v1", IsActive = true });
        db.SaveChanges();
    }

    [Fact]
    public async Task Execute_GroupsEvalScoresByPromptVersion_FileFirst_SkipsFailedResults()
    {
        var scenario = new EvalScenario { Name = "S", PromptKey = Key, UserInput = "in", Criteria = "c" };
        var otherScenario = new EvalScenario { Name = "Khac", PromptKey = "BA/khac.md", UserInput = "in", Criteria = "c" };
        var run = new EvalRun { TargetModelName = "T", JudgeModelName = "J" };
        await using (var db = NewDb())
        {
            db.EvalScenarios.AddRange(scenario, otherScenario);
            db.EvalRuns.Add(run);
            db.EvalResults.AddRange(
                // Hai kết quả đo nội dung FILE (PromptVersionNumber null): 4 và 2 ⇒ TB 3.00.
                new EvalResult { EvalRunId = run.Id, EvalScenarioId = scenario.Id, ScenarioName = "S", Score = 4, IsSuccess = true },
                new EvalResult { EvalRunId = run.Id, EvalScenarioId = scenario.Id, ScenarioName = "S", Score = 2, IsSuccess = true },
                // Một kết quả đo bản DB v1: 5 ⇒ TB 5.00.
                new EvalResult { EvalRunId = run.Id, EvalScenarioId = scenario.Id, ScenarioName = "S", Score = 5, IsSuccess = true, PromptVersionNumber = 1, PromptVersionId = Guid.NewGuid() },
                // Kết quả lỗi (không điểm) và kết quả của template KHÁC — không được tính.
                new EvalResult { EvalRunId = run.Id, EvalScenarioId = scenario.Id, ScenarioName = "S", IsSuccess = false, ErrorMessage = "x" },
                new EvalResult { EvalRunId = run.Id, EvalScenarioId = otherScenario.Id, ScenarioName = "Khac", Score = 1, IsSuccess = true });
            await db.SaveChangesAsync();
        }

        await using var queryDb = NewDb();
        var vm = await NewSut(queryDb).ExecuteAsync(Key);

        Assert.NotNull(vm);
        Assert.Equal(2, vm!.EvalStats.Count);
        Assert.Null(vm.EvalStats[0].VersionNumber);           // "file" đứng đầu như mốc 0
        Assert.Equal(3.00, vm.EvalStats[0].AverageScore);
        Assert.Equal(2, vm.EvalStats[0].ResultCount);
        Assert.Equal(1, vm.EvalStats[1].VersionNumber);
        Assert.Equal(5.00, vm.EvalStats[1].AverageScore);
        Assert.Equal(1, vm.EvalStats[1].ResultCount);
    }

    [Fact]
    public async Task Execute_TemplateWithoutScenarios_HasEmptyEvalStats()
    {
        await using var db = NewDb();
        var vm = await NewSut(db).ExecuteAsync(Key);

        Assert.NotNull(vm);
        Assert.Empty(vm!.EvalStats);
        Assert.False(vm.FileExists);          // template mồ côi
        Assert.Equal(1, vm.ActiveVersionNumber);
        Assert.Equal("v1", vm.ActiveContent);
    }

    private static GetPromptDetailQuery NewSut(AppDbContext db)
    {
        var env = new FakeWebHostEnvironment();
        return new GetPromptDetailQuery(db, new PromptFileCatalog(env), new PromptTemplateService(env));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        // Thư mục không có Prompts/ ⇒ catalog rỗng — mọi key đều "không có file".
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "ico-empty-" + Guid.NewGuid().ToString("N"));
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
