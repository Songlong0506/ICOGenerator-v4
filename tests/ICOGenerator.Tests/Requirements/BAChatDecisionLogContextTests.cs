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

// SOÁT MÂU THUẪN LÀ VIỆC CỦA BA, KHÔNG PHẢI CỦA NGƯỜI DÙNG.
//
// Project.DecisionLog từng có đúng hai chỗ tiêu thụ: panel "Điều đã chốt" cạnh khung chat (người dùng tự
// đọc, tự đối chiếu) và cổng soát mâu thuẫn chạy lúc bấm "Write Requirement" (soát MỘT CỤC ở cuối). Panel
// đã bị gỡ — người dùng chỉ còn gặp nhật ký một lần ở cổng tổng kết cuối — nên nhật ký phải đi vào NGỮ
// CẢNH của lượt chat, nếu không BA mất khả năng bắt mâu thuẫn ngay tại lượt nó xuất hiện.
//
// Đường này KHÔNG tự lộ ra khi hỏng: BA vẫn trả lời trôi chảy, chỉ là không bao giờ hỏi lại khi người dùng
// nói ngược điều đã chốt — và triệu chứng chỉ xuất hiện mãi tới lúc xem bản demo. Vì thế bất biến phải có
// test giữ. Đây cũng là lý do các lượt cũ KHÔNG thay thế được nhật ký: ConversationMemoryService nén chúng
// thành bản tóm tắt, đúng ở hội thoại dài — nơi mâu thuẫn dễ xảy ra nhất.
public class BAChatDecisionLogContextTests : IDisposable
{
    private const string DecisionHeading = "## Điều đã chốt";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public BAChatDecisionLogContextTests()
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
    public async Task DecisionLogOfTheProjectIsLoadedIntoTheChatContext()
    {
        await SeedProjectAsync(
            "- Quản lý duyệt xong thì đơn hoàn tất, không cần cấp cao hơn\n"
            + "- Đơn bị từ chối: nhân viên sửa rồi gửi lại");

        var llm = new FakeLlm();
        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "À mà đơn còn phải qua HR duyệt lần nữa");

        Assert.Equal(ChatWithBAResult.Ok, result.Status);
        var block = Assert.Single(llm.LastChatSystemMessages, m => m.StartsWith(DecisionHeading, StringComparison.Ordinal));
        // Nguyên văn từng quyết định, không phải bản tóm tắt: BA chỉ đối chiếu được mâu thuẫn khi đọc đúng
        // câu đã chốt ("quản lý duyệt xong là hết") để đặt cạnh câu người dùng vừa nói.
        Assert.Contains("Quản lý duyệt xong thì đơn hoàn tất, không cần cấp cao hơn", block);
        Assert.Contains("Đơn bị từ chối: nhân viên sửa rồi gửi lại", block);
    }

    [Fact]
    public async Task EmptyDecisionLog_NoBlockAtAll()
    {
        // Dự án mới / vừa New Chat ⇒ không nhét heading rỗng vào ngữ cảnh: "Điều đã chốt: (chưa có)" là một
        // lời mời model bịa ra vài dòng cho đỡ trống.
        await SeedProjectAsync(null);

        var llm = new FakeLlm();
        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "Mình muốn làm app quản lý đơn nghỉ phép");

        Assert.DoesNotContain(llm.LastChatSystemMessages, m => m.StartsWith(DecisionHeading, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DecisionBlockTellsBaToChallengeContradictionsAndNotReAsk()
    {
        // Danh sách trần không đủ: model đọc một danh sách "điều đã biết" rất dễ làm đúng một trong hai
        // điều SAI — hoặc lờ mâu thuẫn đi, hoặc quay lại hỏi xác nhận từng dòng (đúng cái panel cũ bắt
        // người dùng làm, chỉ là chuyển sang khung chat). Chỉ dẫn phải đi kèm dữ liệu.
        await SeedProjectAsync("- Quản lý duyệt xong thì đơn hoàn tất");

        var llm = new FakeLlm();
        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "Đơn còn phải qua HR duyệt nữa");

        var block = Assert.Single(llm.LastChatSystemMessages, m => m.StartsWith(DecisionHeading, StringComparison.Ordinal));
        Assert.Contains("CHỌI", block);
        Assert.Contains("KHÔNG hỏi lại", block);
    }

    private async Task SeedProjectAsync(string? decisionLog)
    {
        await using var db = NewDb();
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "P",
            Description = "quản lý đơn nghỉ phép",
            DecisionLog = decisionLog
        });
        await db.SaveChangesAsync();
    }

    // Cùng harness dựng BAChatService như BAChatOpenQuestionsContextTests (không scope factory ⇒ các bước
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

    // Mọi lời gọi text (bộ nhớ, hồ sơ user, bản đồ bao phủ, gộp nhật ký) đều fail-open nên để hỏng hết;
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
