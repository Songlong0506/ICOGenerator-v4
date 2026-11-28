using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Organization;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Requirements.Templates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bước sinh AI Design Spec (Approve) KHÔNG BAO GIỜ tự dựng một bản spec thay cho model. Ca thật JD Library:
// spec V2/V3 là khung dự phòng (chép lại Product Brief + "Cần làm rõ") vì câu trả lời bị bọc sai, được duyệt
// và dựng POC mà không có gì báo — trong khi spec thật nằm ngay trong phản hồi. Các test chốt ba tầng:
// (1) phản hồi bọc sai được CỨU, không hỏi lại; (2) không đọc được thì hỏi lại đúng MỘT lần, kèm lời nhắc
// định dạng; (3) vẫn không đọc được thì NÉM — không có tài liệu spec nào được ghi.
public class AiDesignSpecGenerationTests : IDisposable
{
    private const string Version = "V1";
    private const string Brief = "# JD Library\n## Sản phẩm này là gì?\nQuản lý JD của nhà máy.";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _workspaceRoot;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly ProjectArtifactCatalog _catalog = new();

    public AiDesignSpecGenerationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "ico-spec-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);

        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "test" };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = Guid.NewGuid(), AiModelId = model.Id, RoleKey = AgentRoleKey.BusinessAnalyst });
        db.Projects.Add(new Project { Id = _projectId, Name = "JD Library" });
        db.ProjectDocuments.Add(new ProjectDocument
        {
            ProjectId = _projectId,
            FileName = _catalog.ProductBrief.FileName,
            VersionName = Version,
            Content = Brief
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task LeakedParameterTag_IsSalvaged_WithoutRetry()
    {
        // Hình dạng thật (JD Library V2, rút gọn): JSON hợp lệ, object lồng bị viết thành chuỗi mở đầu
        // bằng thẻ tham số của lời gọi tool.
        var llm = new ScriptedLlm(
            """{"assistantMessage": "Đã tạo.", "aiDesignSpec": "\n<parameter name=\"content\"># AI Design Spec\n\n## 6. Screens To Generate\n### 6.1. JD List"}""");

        var spec = await GenerateAsync(llm);

        Assert.StartsWith("# AI Design Spec", spec);
        Assert.Contains("### 6.1. JD List", spec);
        Assert.DoesNotContain("<parameter", spec);
        Assert.Equal(new[] { "BAAiDesignSpec" }, llm.Purposes);
        Assert.Contains("### 6.1. JD List", await SavedSpecAsync());
    }

    [Fact]
    public async Task RewrittenAnswerAfterBrokenOne_IsUsed()
    {
        // Hình dạng thật (JD Library V3, rút gọn): spec đổ thô sau thẻ làm hỏng object đầu, rồi model viết
        // lại trọn câu trả lời thành object hợp lệ ở dòng mới.
        var llm = new ScriptedLlm(
            "{\"assistantMessage\": \"Đã tạo.\", \"aiDesignSpec\": \n<parameter name=\"content\"># AI Design Spec\n\n## 1. Project Goal\nJD \"Library\"\n}\n"
            + """{"assistantMessage": "Đã tạo.", "aiDesignSpec": {"content": "# AI Design Spec\n\n## 1. Project Goal\nJD Library bản viết lại"}}""");

        var spec = await GenerateAsync(llm);

        Assert.Contains("JD Library bản viết lại", spec);
        Assert.Equal(new[] { "BAAiDesignSpec" }, llm.Purposes);
    }

    [Fact]
    public async Task LegacyNestedShape_FromStructuredOutput_IsReadFromText()
    {
        // Prompt cũ (ghi đè qua Prompt Studio) vẫn tả dạng lồng: structured output ra một object có
        // assistantMessage nhưng nội dung rỗng — không được coi là thành công rồi lưu spec rỗng.
        var llm = new ScriptedLlm("""{"assistantMessage": "Đã tạo.", "aiDesignSpec": {"content": "# AI Design Spec\n## 8. Business Rules"}}""")
        {
            StructuredValue = new BAAiDesignSpecResult { AssistantMessage = "Đã tạo." }
        };

        var spec = await GenerateAsync(llm);

        Assert.Contains("## 8. Business Rules", spec);
        Assert.Equal(new[] { "BAAiDesignSpec" }, llm.Purposes);
    }

    [Fact]
    public async Task UnreadableAnswer_RetriesOnce_WithFormatReminder()
    {
        var llm = new ScriptedLlm(
            "Dưới đây là AI Design Spec cho JD Library: # AI Design Spec …",
            """{"assistantMessage": "Đã tạo.", "aiDesignSpecContent": "# AI Design Spec\n## 1. Project Goal\nbản hỏi lại"}""");

        var spec = await GenerateAsync(llm);

        Assert.Contains("bản hỏi lại", spec);
        Assert.Equal(new[] { "BAAiDesignSpec", "BAAiDesignSpecRetry" }, llm.Purposes);
        Assert.EndsWith(RequirementDocsService.SpecFormatReminder, llm.LastUserMessages[1]);
        // Lượt hỏi lại giữ nguyên system và tiền tố prompt (đọc từ cache).
        Assert.StartsWith(llm.LastUserMessages[0], llm.LastUserMessages[1]);
    }

    [Fact]
    public async Task StillUnreadableAfterRetry_Throws_AndSavesNoSpec()
    {
        var llm = new ScriptedLlm("văn xuôi thuần", "vẫn văn xuôi");

        await Assert.ThrowsAsync<InvalidOperationException>(() => GenerateAsync(llm));

        Assert.Equal(new[] { "BAAiDesignSpec", "BAAiDesignSpecRetry" }, llm.Purposes);
        Assert.Null(await SavedSpecAsync());
    }

    private async Task<string> GenerateAsync(ScriptedLlm llm)
    {
        await using var db = NewDb();
        return await NewSut(db, llm).GenerateAiDesignSpecAsync(_projectId, Version);
    }

    private async Task<string?> SavedSpecAsync()
    {
        await using var db = NewDb();
        return await db.ProjectDocuments
            .Where(x => x.ProjectId == _projectId && x.FileName == _catalog.AiDesignSpec.FileName)
            .Select(x => x.Content)
            .FirstOrDefaultAsync();
    }

    private RequirementDocsService NewSut(AppDbContext db, ILlmClient llm)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _workspaceRoot })
            .Build();
        var prompts = new StubPrompts();
        var templates = new RequirementTemplateService(new FakeWebHostEnvironment());

        return new RequirementDocsService(
            db,
            llm,
            templates,
            new RequirementPromptBuilder(),
            new RequirementResponseParser(),
            new RequirementDocumentGenerator(
                db,
                templates,
                new DocxTemplateWriter(),
                _catalog,
                new LocalArtifactStorage(new WorkspacePathResolver(config), NullLogger<LocalArtifactStorage>.Instance)),
            prompts,
            _catalog,
            new OrganizationContextService(db, prompts,
                new OrgChartProvider(db, new MemoryCache(new MemoryCacheOptions())),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_workspaceRoot, true); } catch { /* dọn tạm, lỗi bỏ qua */ }
    }

    // Trả lần lượt các phản hồi đã soạn cho lời gọi spec/hỏi lại; vòng sửa parity (nếu đối chiếu kêu lệch)
    // thất bại để giữ bản vòng đầu — test không phụ thuộc vào bộ đối chiếu.
    private sealed class ScriptedLlm : ILlmClient
    {
        private readonly Queue<string> _responses;
        public readonly List<string> Purposes = new();
        public readonly List<string> LastUserMessages = new();
        public BAAiDesignSpecResult? StructuredValue;

        public ScriptedLlm(params string[] responses) => _responses = new Queue<string>(responses);

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            if (logContext.Purpose == "BAAiDesignSpecParityFix")
                return Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "parity fix not scripted" }, (T?)null));

            Purposes.Add(logContext.Purpose);
            LastUserMessages.Add(messages.Last(m => m.Role == ChatRole.User).Text);
            var content = _responses.Dequeue();
            // Như LlmClient thật: structured value là bản deserialize thẳng từ text (null nếu không khớp T).
            var value = Purposes.Count == 1 && StructuredValue != null
                ? (T?)(object)StructuredValue
                : LlmJson.TryDeserialize<T>(content, requireKnownProperty: true);
            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = content }, value));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub — trả JSON";
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }
}
