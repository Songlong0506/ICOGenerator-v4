using System.ComponentModel;
using System.Runtime.CompilerServices;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Tools.Abstractions;
using ICOGenerator.Services.Tools.Execution;
using ICOGenerator.Services.Tools.Registry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Agents;

// Checkpoint bền của lượt agent. Ca thật đã sinh ra nó: bước POC chết ở bước 10/27 vì chạm hạn mức model,
// bấm "Thử lại" thì dựng lại từ đầu và trả lại toàn bộ số token của 9 bước đã xong. Bất biến ở đây: một
// lượt chạy tiếp gửi lại ĐÚNG hội thoại đã có, KHÔNG chạy lại tool nào đã chạy, và KHÔNG được cấp lại
// nguyên ngân sách bước.
public class AgentRunCheckpointTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly ServiceProvider _services;
    private readonly AgentRunCheckpointStore _store;
    private readonly string _root;
    private readonly IConfiguration _configuration;

    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _modelId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _taskId = Guid.NewGuid();

    public AgentRunCheckpointTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        _root = Path.Combine(Path.GetTempPath(), "agent-checkpoint-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _root })
            .Build();

        // Store tự mở scope riêng cho mỗi lần đọc/ghi — cấp cho nó một container tối thiểu trên cùng kết nối.
        _services = new ServiceCollection()
            .AddScoped(_ => NewDb())
            .BuildServiceProvider();
        _store = new AgentRunCheckpointStore(
            _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AgentRunCheckpointStore>.Instance);

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "Tủ đồ" });
        db.AiModels.Add(new AiModel { Id = _modelId, ModelId = "model-a" });
        db.Agents.Add(new Agent { Id = _agentId, RoleKey = AgentRoleKey.Developer, AiModelId = _modelId });
        db.WorkflowRuns.Add(new WorkflowRun { Id = _runId, ProjectId = _projectId, CurrentStage = WorkflowStageKey.PocPreview });
        db.AgentTasks.Add(new AgentTask
        {
            Id = _taskId, WorkflowRunId = _runId, ProjectId = _projectId, AgentId = _agentId,
            Type = AgentTaskType.PocPreview, Status = AgentTaskStatus.Running, Input = "spec"
        });
        db.SaveChanges();
    }

    // ── Chạy tiếp qua AgentRunService thật ──────────────────────────────────────────────────────────

    // Ca gốc: hai bước gọi tool xong, lời gọi model thứ ba hỏng. Lượt chạy tiếp gửi lại đúng hội thoại đó
    // (prompt gốc + hai cặp gọi tool/kết quả), không chạy lại tool nào, và đánh số bước liền mạch.
    [Fact]
    public async Task Resume_ContinuesTheSameConversation_WithoutRerunningFinishedSteps()
    {
        var notes = new NoteTools();
        var failing = new ScriptedFactory(
            Script.Call("c1", "màn 1"),
            Script.Call("c2", "màn 2"),
            Script.Fail("429 rate limit"));

        await using (var db = NewDb())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => NewAgentRun(db, failing, notes).RunAsync(
                _projectId, _agentId, "PROMPT GỐC", maxSteps: 10,
                checkpoint: new AgentRunCheckpointScope(_taskId)));
        }

        Assert.Equal(new[] { "màn 1", "màn 2" }, notes.Written);
        var resume = await _store.TryLoadAsync(_taskId);
        Assert.NotNull(resume);
        Assert.Equal(2, resume!.CompletedSteps);
        Assert.Equal("model-a", resume.ModelId);

        // Lần thử lại: model trả lời cuối ngay.
        var resumed = new ScriptedFactory(Script.Final("Đã dựng xong."));
        string output;
        var progress = new List<string>();
        await using (var db = NewDb())
        {
            output = await NewAgentRun(db, resumed, notes).RunAsync(
                _projectId, _agentId, "PROMPT MỚI — không được dùng", maxSteps: 10,
                onProgress: (_, message, _) => progress.Add(message),
                checkpoint: new AgentRunCheckpointScope(_taskId, resume));
        }

        Assert.Equal("Đã dựng xong.", output);
        // Không tool nào chạy lại.
        Assert.Equal(new[] { "màn 1", "màn 2" }, notes.Written);

        var request = Assert.Single(resumed.Requests);
        Assert.Equal(
            new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool },
            request.Select(m => m.Role).ToArray());
        Assert.Equal("PROMPT GỐC", request[0].Text);
        Assert.Equal(new[] { "c1", "c2" },
            request.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Select(c => c.CallId).ToArray());
        Assert.Equal(new[] { "c1", "c2" },
            request.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(c => c.CallId).ToArray());

        // Bước được đánh số tiếp (3/10), không quay về 1.
        Assert.Contains(progress, p => p.Contains("3/10"));
        var steps = (await NewDb().AgentModelCallLogs.OrderBy(l => l.CreatedAt).ToListAsync()).Select(l => l.Step).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 3 }, steps);
    }

    // Ngân sách bước là của TASK, không phải của mỗi lần thử: lượt chạy tiếp chỉ có phần còn lại. Ở đây
    // 2/3 bước đã tiêu ⇒ pha đầu chỉ còn MỘT lời gọi model (c3). Hết ngân sách thì vòng lặp dừng ngay sau khi
    // tool của bước đó chạy xong — không có lời gọi model nào thêm — và lời nhắc của pha auto-continue đi KÈM
    // hội thoại đã khôi phục (session không được "quên" phần nạp lại).
    [Fact]
    public async Task Resume_SpendsOnlyTheRemainingBudget_AndKeepsHistoryAcrossPhases()
    {
        var notes = new NoteTools();
        var failing = new ScriptedFactory(Script.Call("c1", "a"), Script.Call("c2", "b"), Script.Fail("mất kết nối"));
        await using (var db = NewDb())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => NewAgentRun(db, failing, notes).RunAsync(
                _projectId, _agentId, "PROMPT GỐC", maxSteps: 3, checkpoint: new AgentRunCheckpointScope(_taskId)));
        }

        var resume = await _store.TryLoadAsync(_taskId);
        var resumed = new ScriptedFactory(Script.Call("c3", "c"), Script.Call("c4", "d"), Script.Final("Xong."));
        await using (var db = NewDb())
        {
            var output = await NewAgentRun(db, resumed, notes).RunAsync(
                _projectId, _agentId, "bỏ qua", maxSteps: 3, checkpoint: new AgentRunCheckpointScope(_taskId, resume));
            Assert.Equal("Xong.", output);
        }

        Assert.Equal(new[] { "a", "b", "c", "d" }, notes.Written);
        // Pha đầu: đúng MỘT lời gọi (c3). Lời gọi thứ hai đã là của pha auto-continue.
        Assert.Equal(3, resumed.Requests.Count);
        var nudge = resumed.Requests[1];
        Assert.Equal("PROMPT GỐC", nudge[0].Text);
        Assert.Equal(new[] { ChatRole.Assistant, ChatRole.Tool, ChatRole.User }, nudge.TakeLast(3).Select(m => m.Role).ToArray());
        Assert.Equal(new[] { "c1", "c2", "c3" },
            nudge.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(c => c.CallId).ToArray());
        Assert.DoesNotContain(resumed.Requests.SelectMany(r => r).SelectMany(m => m.Contents.OfType<FunctionResultContent>()),
            r => r.Result?.ToString() == AgentRunService.SkippedToolResult);
        // Mọi lời gọi đều còn tool: không có lời gọi "ép trả lời không tool" nào bị đốt khi cạn ngân sách.
        Assert.All(resumed.ToolCounts, count => Assert.True(count > 0));
    }

    // Ca thật: vòng tự sửa POC cạn 12 bước, framework gọi model thêm một lần với tool bị gỡ — model đang làm
    // dở nên viết lời gọi tool thành CHỮ, câu trả lời đó bị vứt, rồi mới tới lời nhắc làm tiếp. Lời gọi thừa
    // đó tốn trọn một lượt prompt (hơn 100k token). Nay vòng lặp dừng ngay sau tool của bước cuối.
    [Fact]
    public async Task ExhaustedBudget_StopsRightAfterTheLastStepsTools_WithoutAToolLessCall()
    {
        var notes = new NoteTools();
        var factory = new ScriptedFactory(Script.Call("c1", "a"), Script.Call("c2", "b"), Script.Final("Xong."));
        string output;
        await using (var db = NewDb())
            output = await NewAgentRun(db, factory, notes).RunAsync(_projectId, _agentId, "PROMPT", maxSteps: 2);

        Assert.Equal("Xong.", output);
        Assert.Equal(new[] { "a", "b" }, notes.Written);
        Assert.Equal(3, factory.Requests.Count);
        Assert.All(factory.ToolCounts, count => Assert.True(count > 0));
        Assert.Equal(ChatRole.User, factory.Requests[2][^1].Role);
    }

    // Model trả lời cuối ĐÚNG ở bước cuối của ngân sách là đã xong việc — không phải "cạn bước", không được
    // nhắc làm tiếp.
    [Fact]
    public async Task FinalAnswerOnTheLastBudgetStep_Converges()
    {
        var factory = new ScriptedFactory(Script.Call("c1", "a"), Script.Final("Xong."));
        string output;
        await using (var db = NewDb())
            output = await NewAgentRun(db, factory, new NoteTools()).RunAsync(_projectId, _agentId, "PROMPT", maxSteps: 2);

        Assert.Equal("Xong.", output);
        Assert.Equal(2, factory.Requests.Count);
    }

    // Caller có vòng lặp ngoài (cổng UAT) tắt auto-continue: maxSteps là trần cứng, cạn là sang thẳng lượt
    // salvage không tool — không có pha làm tiếp tới gấp ba số bước.
    [Fact]
    public async Task AutoContinueOff_TreatsMaxStepsAsAHardCap()
    {
        var notes = new NoteTools();
        var factory = new ScriptedFactory(Script.Call("c1", "a"), Script.Call("c2", "b"), Script.Final("Tóm tắt."));
        string output;
        await using (var db = NewDb())
            output = await NewAgentRun(db, factory, notes).RunAsync(_projectId, _agentId, "PROMPT", maxSteps: 2,
                overrides: new AgentRunOverrides(AutoContinue: false));

        Assert.Equal("Tóm tắt.", output);
        Assert.Equal(new[] { "a", "b" }, notes.Written);
        Assert.Equal(3, factory.Requests.Count);
        Assert.Equal(0, factory.ToolCounts[2]);
        Assert.Contains("Đã đạt giới hạn số bước", factory.Requests[2][^1].Text);
    }

    // Lượt chạy chết ngay lời gọi ĐẦU thì chưa có gì để nối: lần thử lại phải là một lượt mới (với bước POC,
    // nghĩa là còn được thử đường dựng một lượt rẻ hơn), không phải "chạy tiếp" một hội thoại rỗng.
    [Fact]
    public async Task FailureOnTheFirstCall_LeavesNothingToResume()
    {
        var failing = new ScriptedFactory(Script.Fail("429"));
        await using (var db = NewDb())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => NewAgentRun(db, failing, new NoteTools()).RunAsync(
                _projectId, _agentId, "PROMPT", maxSteps: 5, checkpoint: new AgentRunCheckpointScope(_taskId)));
        }

        Assert.Null(await _store.TryLoadAsync(_taskId));
    }

    // Không bật checkpoint (biến thể POC, mọi caller cũ) ⇒ không ghi gì.
    [Fact]
    public async Task WithoutACheckpointScope_NothingIsPersisted()
    {
        var factory = new ScriptedFactory(Script.Call("c1", "a"), Script.Final("Xong."));
        await using (var db = NewDb())
            await NewAgentRun(db, factory, new NoteTools()).RunAsync(_projectId, _agentId, "PROMPT", maxSteps: 5);

        Assert.Empty(await NewDb().AgentRunCheckpoints.ToListAsync());
    }

    // ── Store ───────────────────────────────────────────────────────────────────────────────────────

    // App tắt/crash GIỮA lúc tool đang chạy: checkpoint kết thúc bằng lượt assistant gọi tool mà chưa có kết
    // quả. Gửi nguyên như vậy thì endpoint từ chối; đoán "đã xong" hay "chưa chạy" đều có thể sai. Lượt chạy
    // tiếp phải nói thật là không rõ, cho đúng lời gọi còn treo — và không đụng lời gọi đã có kết quả.
    [Fact]
    public async Task InterruptedToolCalls_GetAnExplicitUnknownResult()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "PROMPT"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "Note", new Dictionary<string, object?> { ["text"] = "a" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "ok")]),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("c2", "Note", new Dictionary<string, object?> { ["text"] = "b" }),
                new FunctionCallContent("c3", "Note", new Dictionary<string, object?> { ["text"] = "c" })
            ])
        };
        await _store.SaveAsync(_taskId, messages, completedSteps: 2, modelId: "model-a");

        var resume = await _store.TryLoadAsync(_taskId);

        Assert.NotNull(resume);
        Assert.Equal(5, resume!.Messages.Count);
        var results = resume.Messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();
        Assert.Equal(new[] { "c1", "c2", "c3" }, results.Select(r => r.CallId).ToArray());
        Assert.Equal("ok", results[0].Result?.ToString());
        Assert.Equal(AgentRunCheckpointStore.InterruptedToolResult, results[1].Result?.ToString());
        Assert.Equal(AgentRunCheckpointStore.InterruptedToolResult, results[2].Result?.ToString());
        // Tham số của lời gọi tool sống sót qua vòng ghi-đọc.
        var call = resume.Messages[1].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("a", call.Arguments!["text"]?.ToString());
    }

    // Người ta hay đổi model ngay sau khi một model chạm hạn mức. Suy luận ẩn mang chữ ký của model đã sinh
    // ra nó — gửi cho model khác là hỏng cả request, nên phải gỡ; cùng model thì giữ nguyên.
    [Fact]
    public void ResumingOnADifferentModel_DropsHiddenReasoning()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "PROMPT"),
            new(ChatRole.Assistant, [new TextReasoningContent("nghĩ…"), new FunctionCallContent("c1", "Note")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "ok")]),
            new(ChatRole.Assistant, [new TextReasoningContent("chỉ nghĩ")]),
        };
        var resume = new AgentRunResumePoint(messages, 2, "model-a");

        Assert.Same(messages, resume.MessagesFor("model-a"));

        var other = resume.MessagesFor("model-b");
        Assert.Equal(3, other.Count);
        Assert.DoesNotContain(other, m => m.Contents.Any(c => c is TextReasoningContent));
        Assert.Single(other[1].Contents.OfType<FunctionCallContent>());
    }

    // Hội thoại chứa lại nguyên văn spec và kết quả tool ⇒ mã hoá at rest như call log; và mỗi task chỉ có
    // một dòng, ghi đè.
    [Fact]
    public async Task Save_OverwritesTheSingleRow_AndDeleteRemovesIt()
    {
        await _store.SaveAsync(_taskId, [new ChatMessage(ChatRole.User, "một")], 1, "model-a");
        await _store.SaveAsync(_taskId, [new ChatMessage(ChatRole.User, "hai")], 2, "model-a");

        var row = Assert.Single(await NewDb().AgentRunCheckpoints.ToListAsync());
        Assert.Equal(2, row.CompletedSteps);
        Assert.Contains("hai", row.MessagesJson);

        await _store.DeleteAsync(_taskId);
        Assert.Empty(await NewDb().AgentRunCheckpoints.ToListAsync());
    }

    // Checkpoint hỏng không được chặn task mãi mãi: bỏ nó đi và chạy lại từ đầu.
    [Fact]
    public async Task UnreadableCheckpoint_IsDiscarded()
    {
        await using (var db = NewDb())
        {
            db.AgentRunCheckpoints.Add(new AgentRunCheckpoint { TaskId = _taskId, MessagesJson = "{không phải json", CompletedSteps = 4 });
            await db.SaveChangesAsync();
        }

        Assert.Null(await _store.TryLoadAsync(_taskId));
        Assert.Empty(await NewDb().AgentRunCheckpoints.ToListAsync());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private AgentRunService NewAgentRun(AppDbContext db, IChatClientFactory factory, NoteTools notes)
    {
        var env = new FakeWebHostEnvironment { ContentRootPath = RepoRoot() };
        var prompts = new PromptTemplateService(env);
        var resolver = new WorkspacePathResolver(_configuration);
        var callLogger = new ModelCallLogger(db, new ModelCallImageStore(resolver, _configuration, NullLogger<ModelCallImageStore>.Instance));
        return new AgentRunService(
            db, new NoteRegistry(notes), new ToolPolicyService(), new SilentToolLogger(),
            new AgentPromptBuilder(prompts, new AgentInstructionProvider(prompts)),
            new WorkspaceTools(_configuration, resolver, null!, null!), callLogger,
            factory, NullLoggerFactory.Instance, new NoBudgetLimit(), new LlmSettings(),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)), _store);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ICOGenerator.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Không tìm thấy repo root từ " + AppContext.BaseDirectory);
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Tool duy nhất của agent trong test: ghi lại mỗi lần được CHẠY, để đếm tool có bị chạy lại không.</summary>
    public sealed class NoteTools
    {
        public List<string> Written { get; } = new();

        [Description("Ghi một dòng.")]
        public string Note(string text)
        {
            Written.Add(text);
            return "đã ghi " + text;
        }
    }

    private sealed class NoteRegistry(NoteTools notes) : IToolRegistry
    {
        public Task<IReadOnlyList<ToolRuntimeDescriptor>> GetToolsForAgentAsync(Guid agentId) =>
            Task.FromResult<IReadOnlyList<ToolRuntimeDescriptor>>(
            [
                new ToolRuntimeDescriptor(new ToolDefinition { Name = "Note" }, notes, typeof(NoteTools).GetMethod(nameof(NoteTools.Note))!)
            ]);
    }

    private sealed class SilentToolLogger : IToolExecutionLogger
    {
        public void LogInvocation(ToolRuntimeDescriptor tool) { }
        public void LogResult(ToolRuntimeDescriptor tool, string result) { }
    }

    private sealed class NoBudgetLimit : IBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Một lượt trả lời đã soạn sẵn của model giả: gọi tool, trả lời cuối, hoặc hỏng.</summary>
    private sealed record Script(string? CallId, string? Text, string? Error)
    {
        public static Script Call(string callId, string text) => new(callId, text, null);
        public static Script Final(string text) => new(null, text, null);
        public static Script Fail(string error) => new(null, null, error);
    }

    // Model giả chạy theo kịch bản, mỗi lời gọi tiêu một dòng, và chụp lại message của TỪNG request — đó là
    // thứ test soi để biết lượt chạy tiếp đã gửi gì.
    private sealed class ScriptedFactory(params Script[] script) : IChatClientFactory
    {
        private readonly Queue<Script> _script = new(script);
        public List<List<ChatMessage>> Requests { get; } = new();
        // Số tool quảng bá cho model ở TỪNG request — 0 nghĩa là một lượt bị ép trả lời không tool.
        public List<int> ToolCounts { get; } = new();

        public IChatClient Create(AiModel model) => new Client(this);

        private Script Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            Requests.Add(messages.ToList());
            ToolCounts.Add(options?.Tools?.Count ?? 0);
            return _script.Count > 0 ? _script.Dequeue() : Script.Final("hết kịch bản");
        }

        private sealed class Client(ScriptedFactory owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                var step = owner.Next(messages, options);
                if (step.Error != null)
                    throw new InvalidOperationException(step.Error);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Contents(step))));
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                var step = owner.Next(messages, options);
                await Task.CompletedTask;
                if (step.Error != null)
                    throw new InvalidOperationException(step.Error);
                yield return new ChatResponseUpdate(ChatRole.Assistant, Contents(step));
            }

            private static IList<AIContent> Contents(Script step) => step.CallId != null
                ? [new FunctionCallContent(step.CallId, "Note", new Dictionary<string, object?> { ["text"] = step.Text })]
                : [new TextContent(step.Text)];

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }
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
}
