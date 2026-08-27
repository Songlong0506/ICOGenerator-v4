using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bộ quy ước trình bày là thứ DUY NHẤT chở góp ý giao diện đã được chấp nhận qua một vòng dựng lại POC
// (poc-demo.html bị ghi đè về template ở mỗi vòng dựng mới). Các test chốt: (1) chỉ ghi chú Sent mới được
// chắt lọc, không có thì không gọi LLM; (2) harvest ghi đúng file kèm trích dẫn ghi chú gốc; (3) lỗi LLM
// ⇒ fail-open, bộ cũ còn nguyên; (4) một kết quả NGHÈO HƠN bộ đang có bị từ chối; (5) BuildPromptBlock
// rỗng khi chưa có quy ước nào, nên dự án chưa từng đi đường "chỉnh bản demo" có prompt y như trước.
public class PocUiConventionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _root;
    private readonly WorkspacePathResolver _resolver;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    private const string OneConvention = """
        {"conventions":[{"text":"Nút xác nhận ghi là \"Gửi duyệt\".","screen":"Đơn nghỉ phép","sourceComment":"nút Submit phải đổi thành Gửi duyệt"}]}
        """;

    public PocUiConventionServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        _root = Path.Combine(Path.GetTempPath(), "ico-ui-convention-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _resolver = new WorkspacePathResolver(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _root })
            .Build());

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.SaveChanges();
    }

    [Fact]
    public async Task TryHarvestAsync_NoSentComments_DoesNotCallLlm()
    {
        var project = await SeedAsync(sentComments: 0, openComments: 2);
        var llm = new FakeLlm();

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(0, llm.Calls);
        Assert.False(File.Exists(ConventionPath(project)));
    }

    [Fact]
    public async Task TryHarvestAsync_WithSentComments_WritesConventionsBesidePocDemo()
    {
        var project = await SeedAsync(sentComments: 2, openComments: 1);
        var llm = new FakeLlm { Reply = OneConvention };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(1, llm.Calls);
        // Chỉ ghi chú Sent đi vào prompt — ghi chú còn Open chưa được ai đồng ý.
        Assert.Contains("ghi chú đã gửi 0", llm.LastUserMessage);
        Assert.DoesNotContain("ghi chú open 0", llm.LastUserMessage);

        var stored = Read(project);
        var only = Assert.Single(stored.Conventions);
        Assert.Equal("UI-1", only.Id);
        Assert.Equal("Nút xác nhận ghi là \"Gửi duyệt\".", only.Text);
        Assert.Equal("Đơn nghỉ phép", only.Screen);
        Assert.Equal("nút Submit phải đổi thành Gửi duyệt", only.SourceComment);
    }

    [Fact]
    public async Task TryHarvestAsync_LlmFails_KeepsTheStoredSet()
    {
        var project = await SeedAsync(sentComments: 1, openComments: 0);
        Write(project, "Quy ước cũ.");
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(1, llm.Calls);
        Assert.Equal("Quy ước cũ.", Assert.Single(Read(project).Conventions).Text);
    }

    [Fact]
    public async Task TryHarvestAsync_ResultPoorerThanStoredSet_IsRejected()
    {
        var project = await SeedAsync(sentComments: 1, openComments: 0);
        Write(project, "Quy ước cũ A.", "Quy ước cũ B.");
        // Model chỉ trả về MỘT quy ước dù bộ đang có hai: nó vừa đánh rơi quy ước cũ, không phải người
        // dùng đổi ý — nhận vào là làm bản demo lùi lại.
        var llm = new FakeLlm { Reply = OneConvention };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        var stored = Read(project);
        Assert.Equal(2, stored.Conventions.Count);
        Assert.Equal("Quy ước cũ A.", stored.Conventions[0].Text);
    }

    [Fact]
    public async Task TryHarvestAsync_SendsTheStoredSetToTheModel_SoItCanMergeInsteadOfDuplicating()
    {
        var project = await SeedAsync(sentComments: 1, openComments: 0);
        Write(project, "Quy ước cũ.");
        var llm = new FakeLlm { Reply = OneConvention };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Contains("Quy ước cũ.", llm.LastUserMessage);
    }

    [Fact]
    public void BuildPromptBlock_EmptySet_IsEmpty_SoUntouchedProjectsKeepTheOldPrompt()
    {
        Assert.Equal(string.Empty, PocUiConventionService.BuildPromptBlock(null));
        Assert.Equal(string.Empty, PocUiConventionService.BuildPromptBlock(new PocUiConventionSet()));
    }

    [Fact]
    public void BuildPromptBlock_ListsEveryConvention_UnderItsOwnHeading()
    {
        var block = PocUiConventionService.BuildPromptBlock(new PocUiConventionSet
        {
            Conventions =
            {
                new PocUiConvention { Id = "UI-1", Text = "Nút xác nhận ghi là \"Gửi duyệt\".", Screen = "Đơn nghỉ phép" },
                new PocUiConvention { Id = "UI-2", Text = "Mọi bảng danh sách có ô tìm kiếm." }
            }
        });

        Assert.Contains("# QUY ƯỚC TRÌNH BÀY ĐÃ CHỐT", block);
        Assert.Contains("**UI-1** — màn hình: Đơn nghỉ phép — Nút xác nhận ghi là \"Gửi duyệt\".", block);
        Assert.Contains("**UI-2** — Mọi bảng danh sách có ô tìm kiếm.", block);
    }

    private PocUiConventionService NewSut(AppDbContext db, ILlmClient llm) =>
        new(db, llm, new StubPrompts(), _resolver, new BAAgentResolver(db), NullLogger<PocUiConventionService>.Instance);

    private async Task<Project> SeedAsync(int sentComments, int openComments)
    {
        var ba = new Agent
        {
            Id = Guid.NewGuid(),
            RoleKey = AgentRoleKey.BusinessAnalyst,
            Temperature = 0.2,
            AiModelId = _model.Id
        };
        var project = new Project { Id = Guid.NewGuid(), Name = "Du an quy uoc" };

        await using var db = NewDb();
        db.Agents.Add(ba);
        db.Projects.Add(project);
        for (var i = 0; i < sentComments; i++)
            db.PocComments.Add(NewComment(project.Id, $"ghi chú đã gửi {i}", PocCommentStatus.Sent, i));
        for (var i = 0; i < openComments; i++)
            db.PocComments.Add(NewComment(project.Id, $"ghi chú open {i}", PocCommentStatus.Open, 50 + i));
        await db.SaveChangesAsync();
        return project;
    }

    private static PocComment NewComment(Guid projectId, string comment, PocCommentStatus status, int offsetSeconds) => new()
    {
        ProjectId = projectId,
        PageView = "Đơn nghỉ phép",
        ElementLabel = "Nút Submit",
        Comment = comment,
        Status = status,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(offsetSeconds)
    };

    private string ConventionPath(Project project)
    {
        var mockup = _resolver.GetMockupPath(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));
        return Path.Combine(Path.GetDirectoryName(mockup)!, PocUiConventionService.FileName);
    }

    private void Write(Project project, params string[] texts)
    {
        var path = ConventionPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var set = new PocUiConventionSet
        {
            Conventions = texts.Select((t, i) => new PocUiConvention
            {
                Id = $"UI-{i + 1}",
                Text = t,
                CapturedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }).ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(set));
    }

    private PocUiConventionSet Read(Project project) =>
        JsonSerializer.Deserialize<PocUiConventionSet>(File.ReadAllText(ConventionPath(project)), LlmJson.Options)!;

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* thư mục tạm; dọn được thì dọn */ }
    }

    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public string Reply = OneConvention;
        public bool Fail;
        public string LastUserMessage = string.Empty;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastUserMessage = messages.Last().Text ?? string.Empty;
            return Task.FromResult(new LlmCallResult
            {
                IsSuccess = !Fail,
                Content = Fail ? string.Empty : Reply,
                ErrorMessage = Fail ? "boom" : null
            });
        }

        public async Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => (await ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken), null);
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## chắt lọc quy ước trình bày";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
