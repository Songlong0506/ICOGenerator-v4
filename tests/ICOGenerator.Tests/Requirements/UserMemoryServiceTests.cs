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

// Bộ nhớ CẤP USER: chắt lọc DẦN các sự thật bền về người dùng vào AppUser.UserMemory, gom xuyên các dự án.
// Các test chốt: (1) dưới ngưỡng thì KHÔNG chắt lọc; (2) đủ ngưỡng thì gọi LLM, ghi hồ sơ + dời con trỏ;
// (3) project không có chủ (CreatedByUsername null) thì bỏ qua; (4) chắt lọc lỗi thì fail-open (giữ hồ sơ
// + con trỏ); (5) chắt lọc gom theo lô qua nhiều lần.
public class UserMemoryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Agent _ba;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private const string Owner = "alice";

    public UserMemoryServiceTests()
    {
        _ba = new Agent { Id = Guid.NewGuid(), Name = "BA", Temperature = 0.2, AiModelId = _model.Id };

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(_ba);
        db.AppUsers.Add(new AppUser { Username = Owner, Role = UserRole.User });
        db.SaveChanges();
    }

    [Fact]
    public async Task UpdateAndLoadAsync_BelowThreshold_DoesNotDistill_ReturnsExistingMemory()
    {
        var projectId = await SeedProjectAsync(Owner, turns: 9, existingMemory: "hồ sơ cũ");
        var llm = new FakeLlm();

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var memory = await NewSut(db, llm).UpdateAndLoadAsync(project, _ba, _model);

        Assert.Equal(0, llm.Calls);
        Assert.Equal("hồ sơ cũ", memory);
        Assert.Equal(0, project.UserMemoryHarvestedTurnCount);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_AtThreshold_Distills_WritesMemory_AndMovesPointer()
    {
        var projectId = await SeedProjectAsync(Owner, turns: 10);
        var llm = new FakeLlm { Reply = "Alice là PO ngành bán lẻ, thích tài liệu gạch ý." };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var memory = await NewSut(db, llm).UpdateAndLoadAsync(project, _ba, _model);

        Assert.Equal(1, llm.Calls);
        Assert.Equal("Alice là PO ngành bán lẻ, thích tài liệu gạch ý.", memory);
        Assert.Equal(10, project.UserMemoryHarvestedTurnCount);

        var user = await NewDb().AppUsers.FirstAsync(u => u.Username == Owner);
        Assert.Equal("Alice là PO ngành bán lẻ, thích tài liệu gạch ý.", user.UserMemory);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_ProjectWithoutOwner_IsSkipped()
    {
        var projectId = await SeedProjectAsync(owner: null, turns: 20);
        var llm = new FakeLlm { Reply = "không nên gọi" };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var memory = await NewSut(db, llm).UpdateAndLoadAsync(project, _ba, _model);

        Assert.Equal(0, llm.Calls);
        Assert.Null(memory);
        Assert.Equal(0, project.UserMemoryHarvestedTurnCount);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_WhenDistillFails_FailsOpen_KeepsMemoryAndPointer()
    {
        var projectId = await SeedProjectAsync(Owner, turns: 10, existingMemory: "hồ sơ cũ");
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var memory = await NewSut(db, llm).UpdateAndLoadAsync(project, _ba, _model);

        Assert.Equal(1, llm.Calls);
        Assert.Equal("hồ sơ cũ", memory);
        Assert.Equal(0, project.UserMemoryHarvestedTurnCount);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_DistillsIncrementally_AcrossTwoBatches()
    {
        var projectId = await SeedProjectAsync(Owner, turns: 10);
        var llm = new FakeLlm { Reply = "M1" };

        await using (var db = NewDb())
        {
            var project = await db.Projects.FirstAsync(p => p.Id == projectId);
            await NewSut(db, llm).UpdateAndLoadAsync(project, _ba, _model);
        }

        await AppendTurnsAsync(projectId, from: 10, count: 10);
        llm.Reply = "M2";
        await using (var db = NewDb())
        {
            var project = await db.Projects.FirstAsync(p => p.Id == projectId);
            var memory = await NewSut(db, llm).UpdateAndLoadAsync(project, _ba, _model);
            Assert.Equal("M2", memory);
            Assert.Equal(20, project.UserMemoryHarvestedTurnCount);
        }

        Assert.Equal(2, llm.Calls);
    }

    private UserMemoryService NewSut(AppDbContext db, ILlmClient llm) => new(db, llm, new StubPrompts());

    private async Task<Guid> SeedProjectAsync(string? owner, int turns, string? existingMemory = null)
    {
        var projectId = Guid.NewGuid();
        await using var db = NewDb();
        db.Projects.Add(new Project { Id = projectId, Name = "P", CreatedByUsername = owner });
        if (existingMemory != null)
        {
            var user = await db.AppUsers.FirstAsync(u => u.Username == owner);
            user.UserMemory = existingMemory;
        }
        await db.SaveChangesAsync();
        await AppendTurnsAsync(projectId, from: 0, count: turns);
        return projectId;
    }

    private async Task AppendTurnsAsync(Guid projectId, int from, int count)
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = from; i < from + count; i++)
        {
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = projectId,
                AgentId = _ba.Id,
                Role = i % 2 == 0 ? "user" : "assistant",
                Message = $"turn-{i}",
                CreatedAt = baseTime.AddSeconds(i)
            });
        }
        await db.SaveChangesAsync();
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Fake ILlmClient: chỉ phục vụ đường chắt lọc (ChatWithLogAsync). Đếm số lần gọi và trả/đẩy lỗi theo cấu hình.
    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public string Reply = "memory";
        public bool Fail;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            Calls++;
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
        public override string Get(string relativePath) => "## hồ sơ người dùng";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
