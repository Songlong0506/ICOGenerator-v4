using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Requirements.Templates;
using ICOGenerator.Services.Security;
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

// Cổng readiness phải chạy NGAY trong lượt chat (BAChatService) khi BA định mời bấm "Write Requirement"
// (gate chê thiếu ⇒ thay lời mời bằng câu hỏi của gate), và bước sinh tài liệu (ProductBriefDraftService)
// KHÔNG chạy lại gate khi lượt cuối là lời mời đã được duyệt. Không có cặp hành vi này, hai "giám khảo"
// (BA chat và gate lúc bấm nút) vênh nhau: BA mời bấm nút liên tục, người dùng bấm thì liên tục bị chặn
// "cần bổ sung thông tin".
public class RequirementReadinessGateTests : IDisposable
{
    private const string InviteMessage = "Mình đã đủ thông tin. Nếu không còn gì bổ sung, vui lòng bấm nút \"Write Requirement\" để tạo tài liệu.";
    private const string GateQuestion = "Khi đơn bị từ chối thì xử lý tiếp thế nào?";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public RequirementReadinessGateTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, Name = "BA", RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "app nghỉ phép" });
        db.SaveChanges();
    }

    [Fact]
    public async Task ChatAsync_InviteButGateNotReady_ReplacesInviteWithGateQuestion()
    {
        var llm = new FakeLlm
        {
            ChatReply = new BAChatReply { Message = InviteMessage, Ready = true },
            Readiness = new RequirementReadiness { Ready = false, Message = GateQuestion, Suggestions = new List<string> { "Sửa và gửi lại", "Hủy hẳn đơn" } }
        };

        await using var db = NewDb();
        await NewChatSut(db, llm).ChatAsync(_projectId, "Tôi muốn app quản lý đơn nghỉ phép");

        Assert.Equal(1, llm.ReadinessCalls);
        var lastBaTurn = await LastAssistantTurnAsync();
        // Lời mời bị thay bằng câu hỏi của gate ⇒ nút "Write Requirement" giữ trạng thái mờ (UI nhận
        // diện lời mời qua chuỗi "Write Requirement" trong lượt BA mới nhất) và người dùng được hỏi tiếp
        // ngay trong chat thay vì bấm nút rồi bị chặn.
        Assert.Equal(GateQuestion, lastBaTurn.Message);
        Assert.DoesNotContain("Write Requirement", lastBaTurn.Message, StringComparison.OrdinalIgnoreCase);
        // Suggestions lưu dạng JSON (ký tự tiếng Việt bị escape \uXXXX) — deserialize rồi mới so sánh.
        var suggestions = System.Text.Json.JsonSerializer.Deserialize<List<string>>(lastBaTurn.Suggestions ?? "[]");
        Assert.Equal(new List<string> { "Sửa và gửi lại", "Hủy hẳn đơn" }, suggestions);
    }

    [Fact]
    public async Task ChatAsync_InviteAndGateReady_KeepsInvite()
    {
        var llm = new FakeLlm
        {
            ChatReply = new BAChatReply { Message = InviteMessage, Ready = true },
            Readiness = new RequirementReadiness { Ready = true }
        };

        await using var db = NewDb();
        await NewChatSut(db, llm).ChatAsync(_projectId, "Tôi muốn app quản lý đơn nghỉ phép");

        Assert.Equal(1, llm.ReadinessCalls);
        Assert.Equal(InviteMessage, (await LastAssistantTurnAsync()).Message);
    }

    [Fact]
    public async Task ChatAsync_NormalQuestion_DoesNotCallGate()
    {
        var llm = new FakeLlm
        {
            ChatReply = new BAChatReply { Message = "Đối tượng người dùng chính là ai?", Suggestions = new List<string> { "Nhân viên" } }
        };

        await using var db = NewDb();
        await NewChatSut(db, llm).ChatAsync(_projectId, "Tôi muốn app quản lý đơn nghỉ phép");

        Assert.Equal(0, llm.ReadinessCalls);
        Assert.Equal("Đối tượng người dùng chính là ai?", (await LastAssistantTurnAsync()).Message);
    }

    [Fact]
    public async Task GenerateOrUpdateDraft_LastTurnIsVerifiedInvite_SkipsGate_AndDrafts()
    {
        await SeedTurnsAsync(("user", "Tôi muốn app quản lý đơn nghỉ phép"), ("assistant", InviteMessage));
        var llm = new FakeLlm
        {
            // Van "không giả định" của bước soạn: dừng trước khi ghi file để test không đụng file hệ thống,
            // nhưng vẫn chứng minh đã đi THẲNG vào soạn tài liệu (ProductBriefCalls) mà không qua gate.
            ProductBrief = new BAProductBriefResult { NeedsClarification = true, ClarifyingQuestion = "Còn thiếu một điểm?" }
        };

        await using var db = NewDb();
        var outcome = await NewDraftSut(db, llm).GenerateOrUpdateDraftAsync(_projectId);

        Assert.Equal(0, llm.ReadinessCalls);
        Assert.Equal(1, llm.ProductBriefCalls);
        Assert.Equal(RequirementDraftOutcome.NeedsMoreInfo, outcome);
    }

    [Fact]
    public async Task GenerateOrUpdateDraft_LastTurnIsUserMessage_StillRunsGate()
    {
        await SeedTurnsAsync(("user", "Tôi muốn app quản lý đơn nghỉ phép"), ("assistant", InviteMessage), ("user", "à thêm phần báo cáo nữa"));
        var llm = new FakeLlm
        {
            Readiness = new RequirementReadiness { Ready = false, Message = GateQuestion, Suggestions = new List<string> { "Theo tháng" } }
        };

        await using var db = NewDb();
        var outcome = await NewDraftSut(db, llm).GenerateOrUpdateDraftAsync(_projectId);

        Assert.Equal(1, llm.ReadinessCalls);
        Assert.Equal(0, llm.ProductBriefCalls);
        Assert.Equal(RequirementDraftOutcome.NeedsMoreInfo, outcome);
        Assert.Equal(GateQuestion, (await LastAssistantTurnAsync()).Message);
    }

    private async Task<AgentConversation> LastAssistantTurnAsync()
    {
        await using var db = NewDb();
        return await db.AgentConversations
            .Where(c => c.ProjectId == _projectId && c.Role == "assistant")
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .LastAsync();
    }

    private async Task SeedTurnsAsync(params (string Role, string Message)[] turns)
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < turns.Length; i++)
        {
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = _baId,
                Role = turns[i].Role,
                Message = turns[i].Message,
                CreatedAt = baseTime.AddSeconds(i)
            });
        }
        await db.SaveChangesAsync();
    }

    private static BAChatService NewChatSut(AppDbContext db, ILlmClient llm)
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
            NewOrgContext(db, prompts),
            NewGate(llm, prompts),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new DecisionLogService(db, llm, prompts));
    }

    private static ProductBriefDraftService NewDraftSut(AppDbContext db, ILlmClient llm)
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new StubPrompts();
        var catalog = new ProjectArtifactCatalog();
        var templateService = new RequirementTemplateService(new FakeWebHostEnvironment());
        return new ProductBriefDraftService(
            db,
            llm,
            new RequirementPromptBuilder(),
            new RequirementResponseParser(),
            new RequirementDocumentGenerator(db, templateService, new DocxTemplateWriter(), new WorkspacePathResolver(config), catalog, new FakeArtifactStorage()),
            prompts,
            new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance),
            catalog,
            new ChecklistGapMemoryService(db, llm, prompts),
            new ProductBriefReviewParser(),
            NewOrgContext(db, prompts),
            NewGate(llm, prompts),
            new BAAgentResolver(db),
            new BAConversationLog(db));
    }

    private static RequirementReadinessGate NewGate(ILlmClient llm, PromptTemplateService prompts) =>
        new(llm, prompts, new RequirementReadinessParser(new BAChatReplyParser()));

    // OrgUnits trống trong các test này ⇒ service trả null (fail-open), không thêm system message nào.
    private static OrganizationContextService NewOrgContext(AppDbContext db, PromptTemplateService prompts) =>
        new(db, prompts, new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance);

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Fake ILlmClient trả kết quả structured theo Purpose của lời gọi: lượt chat (BAChat), cổng readiness
    // (BAReadinessCheck) và lượt soạn Product Brief (BAProductBrief). Đếm số lần gọi từng loại để test
    // chốt được gate chạy/không chạy. ChatWithLogAsync (bản đồ bao phủ...) trả lỗi để các service phụ fail-open.
    private sealed class FakeLlm : ILlmClient
    {
        public BAChatReply ChatReply = new() { Message = "Đã ghi nhận." };
        public RequirementReadiness Readiness = new() { Ready = true };
        public BAProductBriefResult? ProductBrief;
        public int ReadinessCalls;
        public int ProductBriefCalls;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            object? value = logContext.Purpose switch
            {
                "BAChat" => ChatReply,
                "BAReadinessCheck" => Readiness,
                "BAProductBrief" => ProductBrief,
                _ => throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}")
            };

            if (logContext.Purpose == "BAReadinessCheck")
                ReadinessCalls++;
            if (logContext.Purpose == "BAProductBrief")
                ProductBriefCalls++;

            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)value));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }

    private sealed class FakeArtifactStorage : IArtifactStorage
    {
        public void InitializeProjectWorkspace(string projectKey) { }
        public string GetDraftPath(string projectKey, ProjectArtifactDescriptor artifact) => Path.Combine(Path.GetTempPath(), artifact.FileName);
        public string GetVersionPath(string projectKey, string versionName, ProjectArtifactDescriptor artifact) => Path.Combine(Path.GetTempPath(), versionName, artifact.FileName);
        public string GetSourceUploadDir(string projectKey) => Path.GetTempPath();
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

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
