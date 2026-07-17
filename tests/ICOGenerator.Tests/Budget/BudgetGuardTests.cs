using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace ICOGenerator.Tests.Budget;

// The circuit breaker must refuse a model call once accumulated USD spend (token × model price, exactly like
// the Usage page) reaches a configured cap — system-wide and per-project — and stay out of the way otherwise.
public class BudgetGuardTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _agentId = Guid.NewGuid();

    // Model priced so 100k prompt + 100k completion tokens = $1 + $3 = $4 per log — easy round numbers for caps.
    private const string ModelId = "m";

    public BudgetGuardTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();

        var model = new AiModel
        {
            ModelId = ModelId,
            Name = "M",
            Endpoint = "http://localhost",
            ApiKey = "",
            InputPricePerMillionTokens = 10m,
            OutputPricePerMillionTokens = 30m
        };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = _agentId, Name = "Dev", AiModelId = model.Id });
        db.SaveChanges();
    }

    [Fact]
    public async Task DoesNotThrow_WhenSpendUnderBothLimits()
    {
        var project = await SeedProjectWithSpendAsync(promptTokens: 100_000, completionTokens: 100_000); // $4

        var guard = Guard(systemUsd: 10m, perProjectUsd: 10m);

        await guard.EnsureWithinBudgetAsync(project); // $4 < $10 both → no throw
    }

    [Fact]
    public async Task Throws_System_WhenSystemSpendReachesLimit()
    {
        var project = await SeedProjectWithSpendAsync(promptTokens: 100_000, completionTokens: 100_000); // $4

        var guard = Guard(systemUsd: 3m, perProjectUsd: 0m);

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(() => guard.EnsureWithinBudgetAsync(project));
        Assert.Equal(BudgetScope.System, ex.Scope);
        Assert.Equal(3m, ex.LimitUsd);
        Assert.Equal(4m, ex.SpentUsd);
    }

    [Fact]
    public async Task Throws_Project_WhenProjectSpendReachesLimit_ButSystemUnlimited()
    {
        var heavy = await SeedProjectWithSpendAsync(promptTokens: 100_000, completionTokens: 100_000); // $4
        var light = await SeedProjectWithSpendAsync(promptTokens: 25_000, completionTokens: 0);        // $0.25

        var guard = Guard(systemUsd: 0m, perProjectUsd: 3m); // system off, per-project $3

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(() => guard.EnsureWithinBudgetAsync(heavy));
        Assert.Equal(BudgetScope.Project, ex.Scope);

        // The cap is PER project: the light project is well under $3 even though the system total is $4.25.
        await guard.EnsureWithinBudgetAsync(light);
    }

    [Fact]
    public async Task DoesNotThrow_WhenNoLimitsConfigured_EvenWithLargeSpend()
    {
        var project = await SeedProjectWithSpendAsync(promptTokens: 100_000_000, completionTokens: 100_000_000); // ~$4000

        var guard = Guard(systemUsd: 0m, perProjectUsd: 0m); // opt-in: nothing set → never blocks

        await guard.EnsureWithinBudgetAsync(project);
    }

    [Fact]
    public async Task IgnoresSpendOutsideThePeriodWindow()
    {
        // A $4 log from two days ago must NOT count against today's Daily cap.
        var project = await SeedProjectWithSpendAsync(promptTokens: 100_000, completionTokens: 100_000,
            createdAt: DateTime.UtcNow.AddDays(-2));

        var guard = Guard(systemUsd: 3m, perProjectUsd: 0m, period: BudgetPeriod.Daily);

        await guard.EnsureWithinBudgetAsync(project); // window starts today → yesterday's spend excluded
    }

    [Fact]
    public async Task SpendSnapshotIsCached_WithinTtl()
    {
        // Guard chạy trước MỖI lời gọi model nên bản tổng chi phí được cache 15s: chi tiêu mới phát sinh
        // trong cửa sổ cache chưa được tính (đánh đổi có chủ đích để khỏi quét bảng log mỗi lời gọi).
        var project = await SeedProjectWithSpendAsync(promptTokens: 25_000, completionTokens: 0); // $0.25

        var guard = Guard(systemUsd: 3m, perProjectUsd: 0m);
        await guard.EnsureWithinBudgetAsync(project); // nạp snapshot $0.25 vào cache

        // Vượt trần NGAY SAU lần kiểm đầu — cùng guard (cùng cache) vẫn cho qua vì snapshot còn tươi…
        await SeedSpendForProjectAsync(project, promptTokens: 100_000, completionTokens: 100_000); // +$4
        await guard.EnsureWithinBudgetAsync(project);

        // …còn guard mới (cache mới — tương đương snapshot hết TTL) thấy đủ $4.25 và chặn.
        await Assert.ThrowsAsync<BudgetExceededException>(() => Guard(systemUsd: 3m, perProjectUsd: 0m).EnsureWithinBudgetAsync(project));
    }

    [Fact]
    public async Task TotalPeriod_CountsHistoricSpend()
    {
        // Same old log, but Total period has no window → it counts and trips the cap.
        var project = await SeedProjectWithSpendAsync(promptTokens: 100_000, completionTokens: 100_000,
            createdAt: DateTime.UtcNow.AddDays(-2));

        var guard = Guard(systemUsd: 3m, perProjectUsd: 0m, period: BudgetPeriod.Total);

        await Assert.ThrowsAsync<BudgetExceededException>(() => guard.EnsureWithinBudgetAsync(project));
    }

    // Mỗi guard một MemoryCache riêng để test không dây dưa số liệu cache của nhau; hành vi cache
    // được kiểm tường minh ở SpendSnapshotIsCached_WithinTtl.
    private BudgetGuard Guard(decimal systemUsd, decimal perProjectUsd, BudgetPeriod period = BudgetPeriod.Monthly)
        => new(NewDb(), new BudgetPolicy(enabled: true, period, systemUsd, perProjectUsd), new MemoryCache(new MemoryCacheOptions()));

    private async Task<Guid> SeedProjectWithSpendAsync(int promptTokens, int completionTokens, DateTime? createdAt = null)
    {
        var projectId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            await db.SaveChangesAsync();
        }
        await SeedSpendForProjectAsync(projectId, promptTokens, completionTokens, createdAt);
        return projectId;
    }

    // Ghi thêm một dòng chi tiêu cho project ĐÃ có — dùng để mô phỏng chi tiêu phát sinh sau lần kiểm đầu.
    private async Task SeedSpendForProjectAsync(Guid projectId, int promptTokens, int completionTokens, DateTime? createdAt = null)
    {
        await using var db = NewDb();
        db.AgentModelCallLogs.Add(new AgentModelCallLog
        {
            ProjectId = projectId,
            AgentId = _agentId,
            ModelId = ModelId,
            ModelName = "M",
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            IsSuccess = true,
            CreatedAt = createdAt ?? DateTime.UtcNow
        });
        await db.SaveChangesAsync();
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
