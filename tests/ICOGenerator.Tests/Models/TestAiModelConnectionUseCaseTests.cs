using ICOGenerator.Application.Models;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Models;

// Nút "Test Connection" ở modal Add/Edit: chỉ gọi endpoint khi dữ liệu đã đủ dùng được, và trên form Edit
// (ApiKey luôn để trống vì key thật không gửi về browser) phải tự lấy key đã lưu — nếu không thì mọi lần
// test từ form Edit đều thất bại oan.
public class TestAiModelConnectionUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _savedModelId = Guid.NewGuid();

    public TestAiModelConnectionUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(new AiModel
        {
            Id = _savedModelId,
            ModelId = "saved-model",
            Endpoint = "http://127.0.0.1:1234/v1",
            ApiKey = "stored-key"
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_WithTypedValues_CallsTesterAndReportsSuccess()
    {
        var tester = new FakeTester(new ModelConnectionTestOutcome(true, 42, 200, "pong"));
        await using var db = NewDb();

        var result = await new TestAiModelConnectionUseCase(db, tester).ExecuteAsync(new AiModel
        {
            ModelId = " qwen3 ",
            Endpoint = " http://127.0.0.1:1234/v1 ",
            ApiKey = " lm-studio "
        });

        Assert.True(result.Ok);
        Assert.Contains("42 ms", result.Message);
        Assert.Equal("pong", result.Detail);
        // Khoảng trắng đầu/cuối do copy-paste không được mang vào lời gọi thật.
        Assert.Equal("qwen3", tester.Probe!.ModelId);
        Assert.Equal("http://127.0.0.1:1234/v1", tester.Probe.Endpoint);
        Assert.Equal("lm-studio", tester.Probe.ApiKey);
    }

    [Fact]
    public async Task ExecuteAsync_WithBlankApiKeyOnExistingModel_UsesStoredKey()
    {
        var tester = new FakeTester(new ModelConnectionTestOutcome(true, 1, 200));
        await using var db = NewDb();

        var result = await new TestAiModelConnectionUseCase(db, tester).ExecuteAsync(new AiModel
        {
            Id = _savedModelId,
            ModelId = "saved-model",
            Endpoint = "http://127.0.0.1:1234/v1",
            ApiKey = ""
        });

        Assert.True(result.Ok);
        Assert.Equal("stored-key", tester.Probe!.ApiKey);
    }

    [Fact]
    public async Task ExecuteAsync_WithFailedCall_ReportsErrorMessageAndDetail()
    {
        var tester = new FakeTester(new ModelConnectionTestOutcome(
            false, 15, 401, "invalid api key", "API trả 401 — ApiKey sai hoặc không có quyền."));
        await using var db = NewDb();

        var result = await new TestAiModelConnectionUseCase(db, tester).ExecuteAsync(new AiModel
        {
            ModelId = "m", Endpoint = "https://api.deepseek.com", ApiKey = "bad"
        });

        Assert.False(result.Ok);
        Assert.Contains("401", result.Message);
        Assert.Equal("invalid api key", result.Detail);
    }

    [Theory]
    [InlineData("", "http://127.0.0.1:1234/v1", "key")]   // thiếu Model ID
    [InlineData("m", "", "key")]                          // thiếu Endpoint
    [InlineData("m", "127.0.0.1:1234", "key")]            // Endpoint không có scheme
    [InlineData("m", "ftp://host/v1", "key")]             // scheme không phải http(s)
    [InlineData("m", "http://127.0.0.1:1234/v1", "")]     // model mới, chưa có ApiKey nào để dùng
    public async Task ExecuteAsync_WithUnusableInput_FailsWithoutCallingTester(string modelId, string endpoint, string apiKey)
    {
        var tester = new FakeTester(new ModelConnectionTestOutcome(true, 1, 200));
        await using var db = NewDb();

        var result = await new TestAiModelConnectionUseCase(db, tester).ExecuteAsync(new AiModel
        {
            ModelId = modelId, Endpoint = endpoint, ApiKey = apiKey
        });

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Message);
        Assert.Null(tester.Probe);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeTester : IModelConnectionTester
    {
        private readonly ModelConnectionTestOutcome _outcome;

        public FakeTester(ModelConnectionTestOutcome outcome) => _outcome = outcome;

        public AiModel? Probe { get; private set; }

        public Task<ModelConnectionTestOutcome> TestAsync(AiModel model, CancellationToken cancellationToken = default)
        {
            Probe = model;
            return Task.FromResult(_outcome);
        }
    }

    // Value-converter của AiModel.ApiKey cần một IApiKeyProtector; mã hóa không liên quan tới các test này.
    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
