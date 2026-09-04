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
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// ĐƯỜNG GHI của "triển vọng phỏng vấn" — nay chỉ còn MỘT danh sách: các ví dụ tính thử đã xác nhận. Danh
// sách câu hỏi đã dời sang lượt chắt lọc bản đồ bao phủ (RequirementCoverageService), nơi nó ra đời cùng
// bản đồ trong một lời gọi; phép chốt nhãn nhóm đi theo nó, xem RequirementCoverageServiceTests.
public class InterviewOutlookServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    public InterviewOutlookServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.SaveChanges();
    }

    // Cột lưu JSON, không phải bullet: chốt luôn ở đây để một lần đổi format nữa không lặng lẽ đi qua.
    [Fact]
    public async Task WorkedExamples_AreStoredAsJson()
    {
        await using var db = NewDb();
        var (project, ba) = await SeedAsync(db);
        var llm = new FakeLlm
        {
            Outlook = new InterviewOutlook { WorkedExamples = { "23 người, sĩ số 8–12 ⇒ mở 2 lớp" } }
        };

        await NewSut(db, llm).UpdateAndLoadAsync(project, ba, _model);

        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.StartsWith("{", reloaded.WorkedExamples!.TrimStart(), StringComparison.Ordinal);
        Assert.Equal("23 người, sĩ số 8–12 ⇒ mở 2 lớp", InterviewOutlookParser.ParseWorkedExamples(reloaded.WorkedExamples).Single());
    }

    // Lượt này KHÔNG còn đụng tới cột câu hỏi. Nó chạy ở HẬU KỲ nên nó luôn cũ hơn bản đồ một lượt; ghi đè
    // danh sách câu hỏi từ đây là dựng lại đúng độ trễ mà lần gộp hai lời gọi vừa bỏ đi.
    [Fact]
    public async Task ItNeverTouchesTheQuestionList()
    {
        await using var db = NewDb();
        var (project, ba) = await SeedAsync(db);
        project.OpenQuestions = OpenQuestionFixture.Stored("[Vòng đời & trạng thái] Chưa rõ trạng thái sau Complete");
        var before = project.OpenQuestions;
        await db.SaveChangesAsync();

        await NewSut(db, new FakeLlm { Outlook = new InterviewOutlook { WorkedExamples = { "một ví dụ" } } })
            .UpdateAndLoadAsync(project, ba, _model);

        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal(before, reloaded.OpenQuestions);
    }

    // Khối "trạng thái hiện có" echo lại cho chính lượt chắt lọc là bullet, không phải JSON — nhét dấu
    // ngoặc nhọn vào prompt vừa tốn token vừa mời model chép cú pháp JSON ra câu trả lời.
    [Fact]
    public async Task TheEchoedState_IsBulletsNotJson()
    {
        await using var db = NewDb();
        var (project, ba) = await SeedAsync(db);
        project.WorkedExamples = InterviewOutlookParser.SerializeWorkedExamples(new[] { "23 người ⇒ mở 2 lớp" });
        await db.SaveChangesAsync();

        var llm = new FakeLlm();
        await NewSut(db, llm).UpdateAndLoadAsync(project, ba, _model);

        Assert.Contains("- 23 người ⇒ mở 2 lớp", llm.LastUserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("\"items\"", llm.LastUserMessage, StringComparison.Ordinal);
    }

    private async Task<(Project Project, Agent Ba)> SeedAsync(AppDbContext db)
    {
        var ba = new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id };
        var project = new Project { Id = Guid.NewGuid(), Name = "P", Description = "d" };
        db.Agents.Add(ba);
        db.Projects.Add(project);
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = project.Id,
            AgentId = ba.Id,
            Role = "user",
            Message = "một lượt mới để lượt chắt lọc có gì mà gộp",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return (project, ba);
    }

    private static InterviewOutlookService NewSut(AppDbContext db, ILlmClient llm)
    {
        var prompts = new StubPrompts();
        return new InterviewOutlookService(db, llm, prompts);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Trả THẲNG structured output: đây là đường mà lượt chắt lọc thật đi.
    private sealed class FakeLlm : ILlmClient
    {
        public InterviewOutlook Outlook = new();
        public string? LastUserMessage;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            LastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            return Task.FromResult(new LlmCallResult { IsSuccess = true, Content = string.Empty });
        }

        public async Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => (await ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken), Outlook as T);
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }

        public override string Get(string relativePath) => "## chắt lọc ví dụ vàng";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
