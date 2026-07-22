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

// "Bản đồ bao phủ yêu cầu" per project: gộp các lượt chat MỚI (kể từ con trỏ) vào bảng trạng thái 12 nhóm,
// lưu trên Project.RequirementCoverageMap. Các test chốt: (1) không có lượt mới thì không gọi LLM, trả bản
// đồ hiện hành; (2) có lượt mới thì gọi LLM một lần, ghi bản đồ + dời con trỏ (bền trong DB); (3) lời gọi
// lỗi thì fail-open — giữ bản đồ cũ, KHÔNG dời con trỏ để lượt sau gộp bù; (4) lần gọi sau chỉ gộp phần delta.
public class RequirementCoverageServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    public RequirementCoverageServiceTests()
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
    public async Task UpdateAndLoadAsync_NoNewTurns_DoesNotCallLlm_ReturnsCurrentMap()
    {
        var (project, ba) = await SeedAsync(turns: 0, existingMap: "- ★ Mục tiêu / bài toán: [RÕ] app kho");
        var llm = new FakeLlm();

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var map = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(0, llm.Calls);
        Assert.Equal("- ★ Mục tiêu / bài toán: [RÕ] app kho", map);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_NewTurns_CallsLlmOnce_SavesMap_AndAdvancesPointer()
    {
        var (project, ba) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Reply = "- ★ Mục tiêu / bài toán: [MỘT PHẦN] còn thiếu: luồng chính" };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var map = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.Equal("- ★ Mục tiêu / bài toán: [MỘT PHẦN] còn thiếu: luồng chính", map);
        Assert.Equal(4, trackedProject.CoverageHarvestedTurnCount);

        // Bền trong DB, không chỉ trên entity đang track.
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal("- ★ Mục tiêu / bài toán: [MỘT PHẦN] còn thiếu: luồng chính", reloaded.RequirementCoverageMap);
        Assert.Equal(4, reloaded.CoverageHarvestedTurnCount);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_WhenLlmFails_FailsOpen_KeepsMapAndPointer()
    {
        var (project, ba) = await SeedAsync(turns: 4, existingMap: "bản đồ cũ", harvestedTurnCount: 2);
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var map = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.Equal("bản đồ cũ", map);
        Assert.Equal(2, trackedProject.CoverageHarvestedTurnCount);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_SecondCallWithoutNewTurns_DoesNotCallLlmAgain()
    {
        var (project, ba) = await SeedAsync(turns: 3);
        var llm = new FakeLlm { Reply = "bản đồ v1" };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);
        var sut = NewSut(db, llm);

        await sut.UpdateAndLoadAsync(trackedProject, trackedBa, _model);
        var map = await sut.UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.Equal("bản đồ v1", map);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_IncludesBaSuggestions_SoReferentialAnswerKeepsContext()
    {
        // Kịch bản bug gốc: BA hỏi kèm 3 gợi ý, user chọn option tham chiếu "Cả hai mục tiêu trên".
        // Khối hội thoại gộp phải chứa các option đã đưa ra, nếu không distill mất context.
        var ba = new Agent { Id = Guid.NewGuid(), Temperature = 0.2, AiModelId = _model.Id };
        var project = new Project { Id = Guid.NewGuid(), Name = "P" };
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var seed = NewDb())
        {
            seed.Agents.Add(ba);
            seed.Projects.Add(project);
            seed.AgentConversations.Add(new AgentConversation
            {
                ProjectId = project.Id,
                AgentId = ba.Id,
                Role = "assistant",
                Message = "Mục tiêu cụ thể của ứng dụng này là gì?",
                Suggestions = "[\"Số hóa quy trình thủ công trên Excel\",\"Chuẩn hóa mẫu JD và quản lý phiên bản\",\"Cả hai mục tiêu trên\"]",
                CreatedAt = baseTime
            });
            seed.AgentConversations.Add(new AgentConversation
            {
                ProjectId = project.Id,
                AgentId = ba.Id,
                Role = "user",
                Message = "Cả hai mục tiêu trên",
                CreatedAt = baseTime.AddSeconds(1)
            });
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm { Reply = "bản đồ v1" };
        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.NotNull(llm.LastUserMessage);
        // Cả hai option "thực" phải xuất hiện để "Cả hai mục tiêu trên" nối được về đúng nội dung.
        Assert.Contains("Số hóa quy trình thủ công trên Excel", llm.LastUserMessage);
        Assert.Contains("Chuẩn hóa mẫu JD và quản lý phiên bản", llm.LastUserMessage);
        Assert.Contains("Cả hai mục tiêu trên", llm.LastUserMessage);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_IncludesSourceFileText_SoMapCreditsAttachedDocs()
    {
        // Bản đồ là nguồn chân lý của cổng "Write Requirement" ⇒ distill phải thấy text tài liệu nguồn,
        // nếu không bản đồ treo [CHƯA HỎI] những thứ tài liệu đính kèm đã trả lời và cổng chặn oan.
        var (project, ba) = await SeedAsync(turns: 2);
        await using (var seed = NewDb())
        {
            seed.ProjectSourceFiles.Add(new ProjectSourceFile
            {
                ProjectId = project.Id,
                FileName = "quy-trinh-duyet.pdf",
                ContentType = "application/pdf",
                ExtractedText = "Quy trình duyệt: nhân viên gửi đơn, trưởng phòng duyệt trong 2 ngày."
            });
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm { Reply = "bản đồ v1" };
        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.NotNull(llm.LastUserMessage);
        Assert.Contains("quy-trinh-duyet.pdf", llm.LastUserMessage);
        Assert.Contains("trưởng phòng duyệt trong 2 ngày", llm.LastUserMessage);
    }

    private RequirementCoverageService NewSut(AppDbContext db, ILlmClient llm) => new(db, llm, new StubPrompts());

    private async Task<(Project Project, Agent Ba)> SeedAsync(int turns, string? existingMap = null, int harvestedTurnCount = 0)
    {
        var ba = new Agent { Id = Guid.NewGuid(), Temperature = 0.2, AiModelId = _model.Id };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "P",
            RequirementCoverageMap = existingMap,
            CoverageHarvestedTurnCount = harvestedTurnCount
        };

        await using var db = NewDb();
        db.Agents.Add(ba);
        db.Projects.Add(project);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < turns; i++)
        {
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = project.Id,
                AgentId = ba.Id,
                Role = i % 2 == 0 ? "user" : "assistant",
                Message = $"turn-{i}",
                CreatedAt = baseTime.AddSeconds(i)
            });
        }
        await db.SaveChangesAsync();
        return (project, ba);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Fake ILlmClient: chỉ phục vụ đường gộp bản đồ (ChatWithLogAsync). Đếm số lần gọi và trả/đẩy lỗi theo cấu hình.
    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public string Reply = "bản đồ bao phủ";
        public bool Fail;

        // Text của lượt user cuối (chính là khối hội thoại được gộp) để test soi xem gợi ý có được đính kèm không.
        public string? LastUserMessage;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            return Task.FromResult(new LlmCallResult
            {
                IsSuccess = !Fail,
                Content = Fail ? string.Empty : Reply,
                ErrorMessage = Fail ? "boom" : null
            });
        }

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => throw new NotSupportedException();
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## cập nhật bản đồ bao phủ";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
