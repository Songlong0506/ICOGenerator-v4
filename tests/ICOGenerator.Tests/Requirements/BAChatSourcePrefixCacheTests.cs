using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Organization;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ICOGenerator.Tests;

namespace ICOGenerator.Tests.Requirements;

// VỊ TRÍ CỦA KHỐI TÀI LIỆU NGUỒN LÀ MỘT QUYẾT ĐỊNH VỀ CHI PHÍ, KHÔNG PHẢI VỀ TRÌNH BÀY.
//
// Prompt cache khớp theo PREFIX: mọi thứ đứng TRƯỚC khối đầu tiên thay đổi trong lượt này được phục vụ
// lại với giá rẻ hơn 10 lần. Text tài liệu nguồn vừa LỚN (tới 20.000 ký tự mỗi file) vừa TĨNH (không đổi
// cho tới khi người dùng upload thêm) — nhưng trước đây nó bị đính vào lượt user CUỐI CÙNG, vị trí biến
// động nhất trong cả danh sách, nên lượt nào cũng trả giá đầy đủ cho đúng những byte không hề đổi.
//
// Đường này KHÔNG tự lộ ra khi hỏng: BA vẫn trả lời đúng như cũ, chỉ là hóa đơn không bao giờ giảm. Vì
// thế bất biến phải có test giữ.
public class BAChatSourcePrefixCacheTests : IDisposable
{
    private const string SourceHeading = "=== TÀI LIỆU NGUỒN DO NGƯỜI DÙNG CUNG CẤP";
    private const string SourceText = "Bảng lương gồm cột Mã NV, Họ tên, Hệ số, Phụ cấp.";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "gpt-5.6-luna", ContextWindow = 1_050_000, SupportsVision = true };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();
    private readonly string _root;

    public BAChatSourcePrefixCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ico-prefix-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "quản lý lương" });
        db.SaveChanges();
    }

    // Trạng thái ỔN ĐỊNH của một project có tài liệu: ảnh chỉ đi kèm cho tới khi BA ghi xong VisionSummary,
    // sau đó mọi lượt đều là lượt không ảnh — tức đây là hình dạng của gần như mọi lượt chat.
    [Fact]
    public async Task TextOnlySource_GoesIntoTheCacheablePrefix_NotOntoTheLastUserTurn()
    {
        await SeedSourceAsync(withImage: false);

        var llm = new FakeLlm();
        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Cho mình hỏi thêm về phụ cấp");

        Assert.Equal(ChatWithBAResult.Ok, result.Status);

        // Khối nguồn phải là một SYSTEM message đứng ngay sau prompt nền — chỗ prefix cache với tới được.
        var index = llm.LastMessages.FindIndex(m => (m.Text ?? string.Empty).Contains(SourceHeading, StringComparison.Ordinal));
        Assert.Equal(1, index);
        Assert.Equal(ChatRole.System, llm.LastMessages[index].Role);
        Assert.Contains(SourceText, llm.LastMessages[index].Text);

        // Và KHÔNG được chép lại lần thứ hai xuống lượt user — chép đôi là trả tiền hai lần cho cùng một thứ.
        Assert.Single(llm.LastMessages, m => (m.Text ?? string.Empty).Contains(SourceHeading, StringComparison.Ordinal));
    }

    // Còn ảnh thì chữ phải Ở LẠI cạnh ảnh của chính nguồn đó: các câu ghi chú ("kèm 1 hình dưới dạng ẢNH")
    // và các mốc [Hình n] chỉ đọc được khi chữ và ảnh còn kề nhau.
    [Fact]
    public async Task SourceWithImages_KeepsTextNextToTheImages_OnTheUserTurn()
    {
        await SeedSourceAsync(withImage: true);

        var llm = new FakeLlm();
        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "Đây là ảnh bảng lương");

        var carrier = Assert.Single(llm.LastMessages,
            m => m.Contents.OfType<TextContent>().Any(t => t.Text.Contains(SourceHeading, StringComparison.Ordinal)));
        Assert.Equal(ChatRole.User, carrier.Role);
        Assert.Contains(carrier.Contents, c => c is DataContent);
    }

    private async Task SeedSourceAsync(bool withImage)
    {
        var storedPath = Path.Combine(_root, "bang-luong.png");
        await File.WriteAllBytesAsync(storedPath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));

        await using var db = NewDb();
        db.ProjectSourceFiles.Add(new ProjectSourceFile
        {
            ProjectId = _projectId,
            // Nguồn ảnh CHƯA có VisionSummary ⇒ ảnh đi kèm; đã có ⇒ chỉ còn chữ (trạng thái ổn định).
            Kind = withImage ? SourceFileKind.Image : SourceFileKind.Document,
            FileName = "bang-luong" + (withImage ? ".png" : ".docx"),
            ContentType = "image/png",
            StoredPath = storedPath,
            IsVisionSource = withImage,
            ExtractedText = SourceText,
        });
        await db.SaveChangesAsync();
    }

    private static BAChatService NewSut(AppDbContext db, ILlmClient llm)
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new StubPrompts();
        return new BAChatService(
            db,
            llm,
            prompts,
            new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance),
            new BAChatReplyParser(),
            new ConversationMemoryService(db, llm, prompts),
            new UserMemoryService(db, llm, prompts),
            new RequirementCoverageService(db, llm, prompts),
            new OrganizationContextService(db, prompts,
                new OrgChartProvider(db, new MemoryCache(new MemoryCacheOptions())),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new DecisionLogService(db, llm, prompts),
            new InterviewOutlookService(db, llm, prompts),
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_root, true); } catch { /* dọn dẹp best-effort */ }
    }

    // Mọi lời gọi text phụ (bộ nhớ, hồ sơ user, bản đồ bao phủ) fail-open nên để hỏng hết; test này chỉ
    // quan tâm hình dạng danh sách message của lượt chat.
    private sealed class FakeLlm : ILlmClient
    {
        public List<ChatMessage> LastMessages = new();

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            if (logContext.Purpose != "BAChat")
                return Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" }, (T?)null));

            LastMessages = messages.ToList();
            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" },
                (T?)(object)new BAChatReply { Message = "Đã ghi nhận." }));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
