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

// XIN FILE NGAY TẠI LƯỢT NGƯỜI DÙNG NHẮC TỚI NÓ.
//
// Luật này nằm trong requirement-chat.v4.md từ lâu, in đậm, kèm một ca thật — và nó vẫn trượt, trượt im
// lặng: không cổng nào biết rằng buổi phỏng vấn vừa bỏ qua một file.
//
// Ca thật (dự án JD Libary 5, lượt 3 và 5): người dùng kể "1 file excel danh sách JD được dùng trong nhà
// máy… và 1 file excel khác để quản lý JD được gán cho nhân viên" — nhắc TỚI HAI LẦN — và không lượt nào
// trong cả 26 lượt xin file. Hậu quả không dừng ở một tệp đính kèm thiếu: không có file thì không có BẢNG
// CỘT để người dùng chốt phạm vi cột, nên toàn bộ mô hình dữ liệu của dự án được dựng từ trí nhớ họ gõ
// tay trong một lượt chat — đúng thứ đang nằm sẵn trong file.
//
// Lượt xin file THAY TRỌN lượt: xin file là lời nhờ HÀNH ĐỘNG, người dùng đọc xong đi tìm file nên mọi
// câu hỏi kèm theo bị nuốt mất, trong khi bản đồ bao phủ vẫn tính là đã hỏi.
public class BAChatSourceRequestTurnTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    private const string Map =
        "- ★ Mục tiêu / bài toán: [RÕ] Quản lý JD và gán cho nhân viên. {nguồn: \"quản lý danh sách JD\"}\n"
        + "- Quy trình hiện tại & điểm khó: [MỘT PHẦN] Đang dùng Excel. còn thiếu: chỗ nào mất thời gian nhất";

    private const string ModelQuestion = "Trong cách làm hiện nay, chỗ nào làm anh/chị mất thời gian nhất?";

    public BAChatSourceRequestTurnTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "app quản lý JD" });
        db.SaveChanges();
    }

    [Fact]
    public async Task MentioningAnExcelFileTakesOverTheTurnWithARequestForIt()
    {
        var llm = new FakeLlm(Map)
        {
            ChatReply = new BAChatReply
            {
                Message = ModelQuestion,
                Suggestions = new List<string> { "Phải sửa tay ở 2 file", "Khó biết JD nào gán cho ai" }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(
            _projectId,
            "hiện tại việc tạo và gán JD được HRBP thực hiện trong file excel, có 1 file excel danh sách JD");

        Assert.Equal(SourceRequestTurn.Message, result.Reply);

        // Chỗ trả lời là ô nhập: họ đính kèm, hoặc nhắn lại là không có file. Chip bấm-là-gửi ở đây sẽ
        // cuốn mất lượt trước khi họ kịp đi tìm file.
        Assert.True(result.OpenEnded);
        Assert.Empty(result.Suggestions);

        // Bản LƯU phải là bản người dùng thấy — chính lượt này là thứ chốt chặn đọc lại để không xin lần hai.
        var saved = await LastAssistantTurnAsync();
        Assert.Equal(result.Reply, saved.Message);
    }

    // Chỉ bắn MỘT lần: giục lần hai là phí đúng cái lượt mà luật này sinh ra để tiết kiệm. Người dùng nói
    // không có file thì hội thoại đi tiếp bình thường.
    [Fact]
    public async Task TheRequestIsNotRepeatedOnceItHasBeenAsked()
    {
        await using (var seed = NewDb())
        {
            seed.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = _baId,
                Role = "assistant",
                Message = SourceRequestTurn.Message,
                CreatedAt = DateTime.UtcNow.AddMinutes(-2)
            });
            seed.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = _baId,
                Role = "user",
                Message = "file đó bên em không gửi ra ngoài được",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1)
            });
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm(Map) { ChatReply = new BAChatReply { Message = ModelQuestion } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "vẫn là file excel đó, HRBP tự sửa");

        Assert.Equal(ModelQuestion, result.Reply);
    }

    // Ranh giới: lượt user không nhắc tới vật mang dữ liệu nào thì câu hỏi của BA được giữ nguyên. Chốt
    // chặn này chỉ đổi lượt khi có một file thật để xin.
    [Fact]
    public async Task ATurnWithoutASourceMentionIsLeftAlone()
    {
        var llm = new FakeLlm(Map) { ChatReply = new BAChatReply { Message = ModelQuestion } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "manager sẽ là người tạo JD cho orgUnit của mình");

        Assert.Equal(ModelQuestion, result.Reply);
    }

    private async Task<AgentConversation> LastAssistantTurnAsync()
    {
        await using var db = NewDb();
        return await db.AgentConversations
            .Where(c => c.ProjectId == _projectId && c.Role == "assistant")
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .FirstAsync();
    }

    // Cùng harness dựng BAChatService như BAChatSilentTurnTests.
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
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        private readonly string? _coverageMap;

        public FakeLlm(string? coverageMap) => _coverageMap = coverageMap;

        public BAChatReply ChatReply = new() { Message = "Đã ghi nhận." };

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            if (logContext.Purpose != "BARequirementCoverage")
                return Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

            return Task.FromResult(_coverageMap == null
                ? new LlmCallResult { IsSuccess = false, ErrorMessage = "distill lỗi" }
                : new LlmCallResult { IsSuccess = true, Content = _coverageMap });
        }

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            if (logContext.Purpose != "BAChat")
                throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}");

            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)(object)ChatReply));
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
