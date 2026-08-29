using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// "Điều đã chốt" per project: gộp các lượt chat MỚI (kể từ con trỏ) vào nhật ký bullet trên
// Project.DecisionLog. Ngoài phần tách dòng, các test ở đây chốt điều kiện để nhật ký ghi được câu
// TỰ ĐỨNG ĐƯỢC: khối gộp phải CHỜM về trước con trỏ, vì hàm chạy cuối lượt chat nên câu hỏi mà lượt user
// đầu lô đang trả lời luôn nằm ở lô TRƯỚC. Thiếu nó, một cú bấm chip ("Chỉ Assistant HR") vào nhật ký
// thành đúng bấy nhiêu chữ — BA ở lượt sau không hiểu, còn soát mâu thuẫn thì bắt nhầm. Nhật ký không còn
// mặt UI nào (bản tổng kết ở cổng tạo tài liệu đã gỡ) nên đây là tầng chặn duy nhất.
public class DecisionLogServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    public DecisionLogServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.SaveChanges();
    }

    [Fact]
    public void ParseItems_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(DecisionLogService.ParseItems(null));
        Assert.Empty(DecisionLogService.ParseItems("  "));
    }

    [Fact]
    public void ParseItems_ReadsBulletsAndSkipsProse()
    {
        var log = """
            - Ứng dụng quản lý đơn nghỉ phép cho ~50 nhân viên
            (ghi chú lạc loài của model)
            - Quản lý duyệt xong thì đơn hoàn tất
            """;

        var items = DecisionLogService.ParseItems(log);

        Assert.Equal(2, items.Count);
        Assert.Equal("Ứng dụng quản lý đơn nghỉ phép cho ~50 nhân viên", items[0]);
        Assert.Equal("Quản lý duyệt xong thì đơn hoàn tất", items[1]);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_IncludesTurnsBeforeCursor_SoChipAnswerKeepsItsQuestion()
    {
        // Đúng hình dạng thật của một lô: con trỏ dừng ngay sau câu hỏi của BA (lượt cuối lô trước), lô mới
        // mở đầu bằng cú bấm chip trả lời câu hỏi đó rồi tới câu hỏi kế của BA.
        var (project, ba) = await SeedChipAnswerAsync();
        var llm = new FakeLlm { Reply = "- Chỉ Assistant HR được tạo project và submit duyệt." };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.NotNull(llm.LastUserMessage);
        // Câu hỏi (nằm TRƯỚC con trỏ) và bộ chip của nó phải có mặt, nếu không "Đúng, chỉ Assistant HR"
        // không nối được về việc gì.
        Assert.Contains("vai trò nào được lập kế hoạch đào tạo", llm.LastUserMessage);
        Assert.Contains("Đúng, chỉ Assistant HR", llm.LastUserMessage);
        // …và phải nói rõ khối đó chỉ để đọc, không phải phần cần chắt (chắt lại = ghi trùng dòng cũ).
        Assert.Contains("KHÔNG chắt lại", llm.LastUserMessage);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_ContextTurnsDoNotMoveCursor_OnlyDeltaCounts()
    {
        var (project, ba) = await SeedChipAnswerAsync();
        var llm = new FakeLlm { Reply = "- Một quyết định" };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        // 4 lượt tất cả, con trỏ cũ là 2 ⇒ chỉ 2 lượt delta được tính; 2 lượt chờm không được đếm lại.
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal(4, reloaded.DecisionHarvestedTurnCount);
        Assert.Equal("- Một quyết định", reloaded.DecisionLog);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_NoNewTurns_DoesNotCallLlm_EvenThoughContextWindowHasTurns()
    {
        // Con trỏ đã bằng số lượt: cửa sổ chờm vẫn kéo về 2 lượt cũ, nhưng delta rỗng ⇒ không được gọi LLM
        // (nếu không, mỗi lần mở trang lại tốn một lời gọi để chắt lại đúng những lượt đã chắt).
        var (project, ba) = await SeedChipAnswerAsync(harvestedTurnCount: 4, existingLog: "- Nhật ký cũ");
        var llm = new FakeLlm();

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var log = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(0, llm.Calls);
        Assert.Equal("- Nhật ký cũ", log);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_WhenLlmFails_FailsOpen_KeepsLogAndCursor()
    {
        var (project, ba) = await SeedChipAnswerAsync(existingLog: "- Nhật ký cũ");
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var log = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal("- Nhật ký cũ", log);
        Assert.Equal(2, trackedProject.DecisionHarvestedTurnCount);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_CursorBeyondRemainingTurns_DoesNotThrow()
    {
        // Đường "sửa lượt cuối" xoá lượt khỏi hội thoại: con trỏ có thể lớn hơn số lượt còn lại, cửa sổ
        // chờm không được cắt âm.
        var (project, ba) = await SeedChipAnswerAsync(harvestedTurnCount: 9);
        var llm = new FakeLlm();

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var log = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(0, llm.Calls);
        Assert.Null(log);
    }

    // Bản đồ bao phủ trích ĐÚNG câu người dùng nói trong lô delta ("Đúng, chỉ Assistant HR") ⇒ lô này chắc
    // chắn có nội dung nghiệp vụ, theo một bộ đọc độc lập với nhật ký.
    private const string MapCitingBatch =
        "- ★ Đối tượng người dùng & vai trò: [RÕ] Assistant HR lập kế hoạch. {nguồn: \"Đúng, chỉ Assistant HR\"}";

    [Fact]
    public async Task SuspectedMiss_ChattsAgainOnce_WithEvidenceHint_AndTakesTheSecondPass()
    {
        // Lượt chắt đầu trả về Y HỆT nhật ký cũ trong khi lô có bằng chứng — đúng hình dạng ca JD Libary 5.
        var (project, ba) = await SeedChipAnswerAsync(existingLog: "- Nhật ký cũ một dòng", coverageMap: MapCitingBatch);
        var llm = new FakeLlm();
        llm.Replies.Enqueue("- Nhật ký cũ một dòng");
        llm.Replies.Enqueue("- Nhật ký cũ một dòng\n- Chỉ Assistant HR được lập kế hoạch đào tạo và submit duyệt.");

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var log = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(2, llm.Calls);
        // Lượt chắt lại mang purpose RIÊNG: guard mới thì phải đếm được nó nổ bao nhiêu lần.
        Assert.Equal(new[] { "BADecisionLog", "BADecisionLogRecheck" }, llm.Purposes);
        // Chỉ dẫn phải chở ĐÚNG câu bộ đọc kia đã trích, không phải một lời "cố lên" chung chung.
        Assert.Contains("RÀ LẠI", llm.UserMessages[1]);
        Assert.Contains("Đúng, chỉ Assistant HR", llm.UserMessages[1]);
        // …và không được nới luật chống suy diễn khi giục model ghi thêm.
        Assert.Contains("KHÔNG suy diễn", llm.UserMessages[1]);
        Assert.Contains("Chỉ Assistant HR được lập kế hoạch", log);
    }

    [Fact]
    public async Task SuspectedMiss_SecondPassStillUnchanged_KeepsFirstPass_AndAdvancesCursor()
    {
        // Rà lại mà vẫn không có gì ⇒ chấp nhận, dời con trỏ như thường. Nghi ngờ không được thành vòng lặp.
        var (project, ba) = await SeedChipAnswerAsync(existingLog: "- Nhật ký cũ một dòng", coverageMap: MapCitingBatch);
        var llm = new FakeLlm { Reply = "- Nhật ký cũ một dòng" };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var log = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(2, llm.Calls);
        Assert.Equal("- Nhật ký cũ một dòng", log);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal(4, reloaded.DecisionHarvestedTurnCount);
    }

    [Fact]
    public async Task SuspectedMiss_SecondPassFails_FailsOpenToFirstPass()
    {
        var (project, ba) = await SeedChipAnswerAsync(existingLog: "- Nhật ký cũ một dòng", coverageMap: MapCitingBatch);
        var llm = new FakeLlm { Reply = "- Nhật ký cũ một dòng", FailFromCall = 2 };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var log = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(2, llm.Calls);
        Assert.Equal("- Nhật ký cũ một dòng", log);
    }

    [Fact]
    public async Task LogGrew_DoesNotChatAgain()
    {
        // Lô có bằng chứng NHƯNG nhật ký đã dài thêm ⇒ không nghi ngờ gì, không tốn lời gọi thứ hai.
        var (project, ba) = await SeedChipAnswerAsync(existingLog: "- Nhật ký cũ một dòng", coverageMap: MapCitingBatch);
        var llm = new FakeLlm { Reply = "- Nhật ký cũ một dòng\n- Chỉ Assistant HR được lập kế hoạch đào tạo." };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.Equal(new[] { "BADecisionLog" }, llm.Purposes);
    }

    [Fact]
    public async Task NoCoverageEvidence_DoesNotChatAgain_EvenWhenLogUnchanged()
    {
        // Không có bản đồ (hoặc bản đồ chưa trích được gì từ lô) ⇒ guard không có căn cứ, im lặng. Đây là
        // ca thường gặp nhất — lô toàn lượt xã giao — nên nó KHÔNG được đẻ ra lời gọi thứ hai.
        var (project, ba) = await SeedChipAnswerAsync(existingLog: "- Nhật ký cũ một dòng");
        var llm = new FakeLlm { Reply = "- Nhật ký cũ một dòng" };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
    }

    private DecisionLogService NewSut(AppDbContext db, ILlmClient llm) => new(db, llm, new StubPrompts());

    // Bốn lượt: [user kể, BA hỏi kèm chip] đã gộp (con trỏ mặc định = 2), rồi [user bấm chip, BA hỏi tiếp].
    private async Task<(Project Project, Agent Ba)> SeedChipAnswerAsync(int harvestedTurnCount = 2, string? existingLog = null, string? coverageMap = null)
    {
        var ba = new Agent { Id = Guid.NewGuid(), Temperature = 0.2, AiModelId = _model.Id };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "P",
            DecisionLog = existingLog,
            RequirementCoverageMap = coverageMap,
            DecisionHarvestedTurnCount = harvestedTurnCount
        };
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using var db = NewDb();
        db.Agents.Add(ba);
        db.Projects.Add(project);
        db.AgentConversations.Add(NewTurn(project.Id, ba.Id, "user", "Bên mình lập kế hoạch đào tạo hằng năm.", baseTime));
        db.AgentConversations.Add(NewTurn(project.Id, ba.Id, "assistant",
            "Mình chốt: vai trò nào được lập kế hoạch đào tạo và submit duyệt?", baseTime.AddSeconds(1),
            suggestions: "[\"Đúng, chỉ Assistant HR\",\"Không, có vai trò khác\"]"));
        db.AgentConversations.Add(NewTurn(project.Id, ba.Id, "user", "Đúng, chỉ Assistant HR", baseTime.AddSeconds(2)));
        db.AgentConversations.Add(NewTurn(project.Id, ba.Id, "assistant",
            "Khi có chỗ trống trong lớp thì xử lý waitlist thế nào?", baseTime.AddSeconds(3)));
        await db.SaveChangesAsync();

        return (project, ba);
    }

    private static AgentConversation NewTurn(Guid projectId, Guid agentId, string role, string message, DateTime createdAt, string? suggestions = null)
        => new()
        {
            ProjectId = projectId,
            AgentId = agentId,
            Role = role,
            Message = message,
            Suggestions = suggestions,
            CreatedAt = createdAt
        };

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Fake ILlmClient: chỉ phục vụ đường gộp nhật ký (ChatWithLogAsync); giữ lại text lượt user cuối để
    // test soi đúng khối hội thoại đã gửi đi.
    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public string Reply = "- quyết định";
        public bool Fail;
        // Lời gọi thứ N trở đi hỏng — để test đường fail-open của riêng lượt chắt lại.
        public int FailFromCall = int.MaxValue;
        public string? LastUserMessage;
        // Lượt chắt LẠI (guard nghi bỏ sót) là lời gọi thứ hai: xếp sẵn câu trả lời riêng cho nó, và giữ
        // purpose của từng lời gọi để test chốt được nó mang nhãn riêng trên AI Call Logs.
        public readonly Queue<string> Replies = new();
        public readonly List<string> Purposes = new();
        public readonly List<string> UserMessages = new();

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Purposes.Add(logContext.Purpose);
            LastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            UserMessages.Add(LastUserMessage ?? string.Empty);
            var reply = Replies.Count > 0 ? Replies.Dequeue() : Reply;
            var failed = Fail || Calls >= FailFromCall;
            return Task.FromResult(new LlmCallResult
            {
                IsSuccess = !failed,
                Content = failed ? string.Empty : reply,
                ErrorMessage = failed ? "boom" : null
            });
        }

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => throw new NotSupportedException();
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## cập nhật nhật ký điều đã chốt";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
