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

// ĐƯỜNG GHI của "triển vọng phỏng vấn". Nhóm của một điểm tồn đọng do MODEL điền, nhưng nó là đầu vào của
// một chốt chặn TẤT ĐỊNH (CoveragePendingGuard đối chiếu nó với nhãn dòng bản đồ bao phủ) — nên nó phải
// được chốt về đúng một trong 12 nhãn checklist NGAY Ở ĐÂY, chứ không để mỗi tầng đọc tự đoán lấy. Trước
// đây nhãn này là một thẻ "[…]" model tự gõ ở đầu chuỗi và ba chỗ đọc đều phải regex bóc lại.
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

    // Model viết gọn một nhãn ("Luồng ngoại lệ" cho «Luồng ngoại lệ & trường hợp đặc biệt") ⇒ vẫn phải ra
    // đúng nhãn checklist. Không snap ở đây thì phép đối chiếu của guard phụ thuộc vào việc model gõ trùng
    // từng chữ với bản đồ — đúng cái mong manh mà thẻ chuỗi cũ đã mắc.
    [Fact]
    public async Task AGroupWrittenLoosely_IsSnappedToTheChecklistLabel()
    {
        var stored = await HarvestAsync(new OpenQuestionEntry
        {
            Group = "Luồng ngoại lệ",
            Text = "Chưa rõ đăng ký lại sau khi bị Reject"
        });

        var item = Assert.Single(InterviewOutlookParser.ParseOpenQuestions(stored));
        Assert.Equal("Luồng ngoại lệ & trường hợp đặc biệt", item.Group);
        Assert.Equal("Chưa rõ đăng ký lại sau khi bị Reject", item.Text);
    }

    // Nhãn model tự nghĩ ra không khớp nhóm nào ⇒ để RỖNG. Fail-open: mục vẫn nằm trong danh sách để BA
    // hỏi, chỉ không hạ được dòng bản đồ nào — guard không được phép hạ nhầm vì một nhãn vô nghĩa.
    [Fact]
    public async Task AGroupThatMatchesNothing_IsBlanked_ButTheItemSurvives()
    {
        var stored = await HarvestAsync(new OpenQuestionEntry
        {
            Group = "Tích hợp hệ thống ngoài",
            Text = "Chưa rõ nối với SAP kiểu gì"
        });

        var item = Assert.Single(InterviewOutlookParser.ParseOpenQuestions(stored));
        Assert.Equal(string.Empty, item.Group);
        Assert.Equal("Chưa rõ nối với SAP kiểu gì", item.Text);
    }

    // Cột lưu JSON, không phải bullet: chốt luôn ở đây để một lần đổi format nữa không lặng lẽ đi qua.
    [Fact]
    public async Task BothColumns_AreStoredAsJson()
    {
        await using var db = NewDb();
        var (project, ba) = await SeedAsync(db);
        var llm = new FakeLlm
        {
            Outlook = new InterviewOutlook
            {
                OpenQuestions = { new OpenQuestionEntry { Group = "Vòng đời & trạng thái", Text = "Chưa rõ trạng thái sau Complete" } },
                WorkedExamples = { "23 người, sĩ số 8–12 ⇒ mở 2 lớp" }
            }
        };

        await NewSut(db, llm).UpdateAndLoadAsync(project, ba, _model);

        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.StartsWith("{", reloaded.OpenQuestions!.TrimStart(), StringComparison.Ordinal);
        Assert.StartsWith("{", reloaded.WorkedExamples!.TrimStart(), StringComparison.Ordinal);
        Assert.Equal("Vòng đời & trạng thái", InterviewOutlookParser.ParseOpenQuestions(reloaded.OpenQuestions).Single().Group);
        Assert.Equal("23 người, sĩ số 8–12 ⇒ mở 2 lớp", InterviewOutlookParser.ParseWorkedExamples(reloaded.WorkedExamples).Single());
    }

    // Khối "trạng thái hiện có" echo lại cho chính lượt chắt lọc phải mang CẶP nhóm↔câu hỏi: thiếu nhóm thì
    // model gán lại mục cũ sang nhóm khác ở lượt gộp sau. Và nó là bullet, không phải JSON — nhét dấu ngoặc
    // nhọn vào prompt vừa tốn token vừa mời model chép cú pháp JSON ra câu trả lời.
    [Fact]
    public async Task TheEchoedStateCarriesTheGroup_AsBulletsNotJson()
    {
        await using var db = NewDb();
        var (project, ba) = await SeedAsync(db);
        project.OpenQuestions = OpenQuestionFixture.Stored("[Vòng đời & trạng thái] Chưa rõ trạng thái sau Complete");
        await db.SaveChangesAsync();

        var llm = new FakeLlm();
        await NewSut(db, llm).UpdateAndLoadAsync(project, ba, _model);

        Assert.Contains("- [Vòng đời & trạng thái] Chưa rõ trạng thái sau Complete", llm.LastUserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("\"group\"", llm.LastUserMessage, StringComparison.Ordinal);
    }

    private async Task<string?> HarvestAsync(OpenQuestionEntry entry)
    {
        await using var db = NewDb();
        var (project, ba) = await SeedAsync(db);

        await NewSut(db, new FakeLlm { Outlook = new InterviewOutlook { OpenQuestions = { entry } } })
            .UpdateAndLoadAsync(project, ba, _model);

        return (await NewDb().Projects.FirstAsync(p => p.Id == project.Id)).OpenQuestions;
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
        return new InterviewOutlookService(db, llm, prompts, new CoverageChecklist(prompts));
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

    // Checklist 12 nhóm được bóc từ prompt bao phủ THẬT — phép snap nhãn phải chạy trên đúng bộ nhãn mà
    // production dùng, không phải một danh sách chép tay trong test.
    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }

        public override string Get(string relativePath)
            => relativePath == CoverageChecklist.CoveragePromptPath
                ? ReadRealPrompt(relativePath)
                : "## chắt lọc triển vọng phỏng vấn";

        // Cùng cách tìm Prompts/ như InterviewDeadEndRuleTests: ưu tiên bản copy trong bin, không có thì
        // đi ngược lên repo root.
        private static string ReadRealPrompt(string promptKey)
        {
            var relative = promptKey.Replace('/', Path.DirectorySeparatorChar);

            var fromBin = Path.Combine(AppContext.BaseDirectory, "Prompts", relative);
            if (File.Exists(fromBin))
                return File.ReadAllText(fromBin);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Prompts", relative)))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir!.FullName, "Prompts", relative));
        }
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
