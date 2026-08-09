using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
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

namespace ICOGenerator.Tests.Requirements;

// "ĐIỂM CẦN LÀM RÕ" LÀ VIỆC CỦA BA, KHÔNG PHẢI BÀI TẬP VỀ NHÀ CỦA NGƯỜI DÙNG.
//
// Trước đây Project.OpenQuestions chỉ có một chỗ tiêu thụ duy nhất: panel "Điểm cần làm rõ" cạnh khung
// chat — user tự đọc danh sách rồi tự bấm để trả lời. Panel đã bị bỏ (BA thắc mắc gì thì hỏi thẳng trong
// chat), nên danh sách phải đi vào NGỮ CẢNH lượt chat, nếu không phần tồn đọng rơi mất im lặng: bản đồ
// bao phủ chỉ phân giải theo NHÓM, không giữ được "Reference Belt đồng bộ tự động hay nhập tay?".
//
// Hai bất biến dưới đây là thứ giữ đường dẫn đó sống, vì nó không còn hiển thị ở đâu để ai nhìn thấy khi hỏng.
public class BAChatOpenQuestionsContextTests : IDisposable
{
    private const string OpenQHeading = "## Điểm cần làm rõ còn tồn đọng";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public BAChatOpenQuestionsContextTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.SaveChanges();
    }

    [Fact]
    public async Task OpenQuestionsOfTheProjectAreLoadedIntoTheChatContext()
    {
        await SeedProjectAsync(
            "- Nguồn dữ liệu Reference Belt: đồng bộ tự động hay nhập thủ công?\n"
            + "- Ai được tạo/sửa/xóa Belt Type?");

        var llm = new FakeLlm();
        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Mình muốn quản lý Reference Belt");

        Assert.Equal(ChatWithBAResult.Ok, result.Status);
        var block = Assert.Single(llm.LastChatSystemMessages, m => m.StartsWith(OpenQHeading, StringComparison.Ordinal));
        // Nguyên văn từng mục, không phải chỉ con số đếm: BA phải hỏi ĐÚNG điểm còn treo.
        Assert.Contains("Nguồn dữ liệu Reference Belt: đồng bộ tự động hay nhập thủ công?", block);
        Assert.Contains("Ai được tạo/sửa/xóa Belt Type?", block);
    }

    [Fact]
    public async Task NoOpenQuestions_NoBlockAtAll()
    {
        // Danh sách rỗng (dự án mới / mọi điểm đã chốt) ⇒ không nhét một heading rỗng vào ngữ cảnh: model
        // đọc "còn tồn đọng: (không có)" rất dễ hiểu thành "đã đủ, mời bấm Write Requirement".
        await SeedProjectAsync(null);

        var llm = new FakeLlm();
        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "Mình muốn quản lý Reference Belt");

        Assert.DoesNotContain(llm.LastChatSystemMessages, m => m.StartsWith(OpenQHeading, StringComparison.Ordinal));
    }

    private async Task SeedProjectAsync(string? openQuestions)
    {
        await using var db = NewDb();
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "P",
            Description = "quản lý Reference Belt",
            OpenQuestions = openQuestions
        });
        await db.SaveChangesAsync();
    }

    // Cùng harness dựng BAChatService như BAChatRepeatedQuestionTests (không scope factory ⇒ các bước
    // chuẩn bị chạy tuần tự trên chính db của test).
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
            new OrganizationContextService(db, prompts, new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new DecisionLogService(db, llm, prompts),
            new InterviewOutlookService(db, llm, prompts),
            new ChecklistNoteStore(db),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Mọi lời gọi text (bộ nhớ, hồ sơ user, bản đồ bao phủ, decision log) đều fail-open nên để hỏng hết;
    // test này chỉ quan tâm bộ system message đi vào lượt chat.
    private sealed class FakeLlm : ILlmClient
    {
        public List<string> LastChatSystemMessages = new();

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            if (logContext.Purpose != "BAChat")
                return Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" }, (T?)null));

            LastChatSystemMessages = messages
                .Where(m => m.Role == ChatRole.System)
                .Select(m => m.Text ?? string.Empty)
                .ToList();

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
