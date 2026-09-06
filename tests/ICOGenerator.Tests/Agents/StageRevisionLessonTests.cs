using ICOGenerator.Application.Agents;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ICOGenerator.Tests;

namespace ICOGenerator.Tests.Agents;

// Đường học của các VAI KỸ THUẬT: nhận xét người duyệt gõ ở nút "Yêu cầu chỉnh sửa" của một bước delivery
// trở thành bài học cho vai đã chạy bước đó, nạp lại vào prompt của vai ở mọi dự án sau.
//
// Bốn điều phải đúng, vì mỗi điều là một cách cơ chế này gây hại nếu sai:
//   (1) chỉ học khi người duyệt ĐÃ DUYỆT bước đó — học từ một bước bị bỏ dở là học từ bản vá chưa ai
//       xác nhận là đạt;
//   (2) bài học vào ĐÚNG vai (checklist Developer không lẫn vào prompt Technical Lead) và vào bucket
//       CHUNG (bài học kỹ thuật không phụ thuộc phòng ban);
//   (3) duyệt thẳng không góp ý ⇒ KHÔNG tốn lời gọi LLM nào — im lặng là "ổn", không phải tín hiệu;
//   (4) fail-open: lời gọi lỗi thì hàng đợi đứng yên để task sau gộp bù, không đốt bằng chứng.
public class StageRevisionLessonTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _runId = Guid.NewGuid();
    private Guid _techLeadId;
    private Guid _developerId;

    private const string OneLesson = """
        {"items":[{"text":"Mọi lời gọi ra dịch vụ ngoài phải có xử lý lỗi và giá trị dự phòng.","rationale":"Kiến trúc đề xuất gọi thẳng API tỉ giá mà không nói gì tới trường hợp dịch vụ chết.","evidence":"gọi API tỉ giá mà không có phương án khi nó chết"}]}
        """;

    public StageRevisionLessonTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();

        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "test" };
        db.AiModels.Add(model);

        // Đủ vai để cổng duyệt enqueue được bước kế của mọi kịch bản bên dưới.
        foreach (var role in new[] { AgentRoleKey.BusinessAnalyst, AgentRoleKey.TechLead, AgentRoleKey.Developer, AgentRoleKey.Tester })
        {
            var agent = new Agent { Id = Guid.NewGuid(), RoleKey = role, AiModelId = model.Id };
            db.Agents.Add(agent);
            if (role == AgentRoleKey.TechLead) _techLeadId = agent.Id;
            if (role == AgentRoleKey.Developer) _developerId = agent.Id;
        }

        db.Projects.Add(new Project { Id = _projectId, Name = "Đổi tiền", BackendGitUrl = "http://git/be", FrontendGitUrl = "http://git/fe" });
        db.SaveChanges();
    }

    // ── Cổng duyệt: ai được vào hàng đợi ─────────────────────────────────────────────────────────────

    // Duyệt một bước từng bị góp ý ⇒ CHỈ nhận xét của chính bước đó vào hàng đợi. Task chạy trơn (không
    // nhận xét) và nhận xét của bước KHÁC trong cùng run đều phải đứng ngoài — nếu không, bài học của
    // Technical Lead sẽ bị nhét thêm nhận xét dành cho Developer.
    [Fact]
    public async Task ApproveStage_QueuesOnlyRevisionFeedbackOfTheApprovedStage()
    {
        await SeedWaitingAsync(WorkflowStageKey.ArchitectureDesign);
        var revisedId = await AddTaskAsync(AgentTaskType.ArchitectureDesign, _techLeadId, "thiếu phương án khi API tỉ giá chết");
        var cleanId = await AddTaskAsync(AgentTaskType.ArchitectureDesign, _techLeadId, feedback: null);
        var otherStageId = await AddTaskAsync(AgentTaskType.PocPreview, _developerId, "nút Lưu để bên phải");

        await using (var db = NewDb())
            Assert.Equal(ApproveStageResult.Advanced, await NewApprove(db).ExecuteAsync(_projectId, _runId));

        await using var check = NewDb();
        Assert.True((await check.AgentTasks.FirstAsync(t => t.Id == revisedId)).PendingLessonHarvest);
        Assert.False((await check.AgentTasks.FirstAsync(t => t.Id == cleanId)).PendingLessonHarvest);
        Assert.False((await check.AgentTasks.FirstAsync(t => t.Id == otherStageId)).PendingLessonHarvest);
    }

    // Bước CUỐI (Pull Request) không còn bước kế nên cổng duyệt đi nhánh "hoàn tất" — nhánh đó cũng phải
    // bật cờ, nếu không nhận xét ở bước cuối vĩnh viễn không được học.
    [Fact]
    public async Task ApproveStage_LastStage_StillQueuesFeedback()
    {
        await SeedWaitingAsync(WorkflowStageKey.PullRequest);
        var revisedId = await AddTaskAsync(AgentTaskType.PullRequest, _developerId, "nhánh phải đặt tên theo mã ticket");

        await using (var db = NewDb())
            Assert.Equal(ApproveStageResult.Completed, await NewApprove(db).ExecuteAsync(_projectId, _runId));

        await using var check = NewDb();
        Assert.True((await check.AgentTasks.FirstAsync(t => t.Id == revisedId)).PendingLessonHarvest);
    }

    // Người duyệt bấm Duyệt thẳng, chưa từng góp ý gì: không có bằng chứng nào, hàng đợi phải rỗng. Đây là
    // hành vi ĐÚNG chứ không phải thiếu sót — im lặng ở cổng duyệt kỹ thuật nghĩa là "đạt".
    [Fact]
    public async Task ApproveStage_NoFeedback_QueuesNothing()
    {
        await SeedWaitingAsync(WorkflowStageKey.ArchitectureDesign);
        await AddTaskAsync(AgentTaskType.ArchitectureDesign, _techLeadId, feedback: null);

        await using (var db = NewDb())
            await NewApprove(db).ExecuteAsync(_projectId, _runId);

        await using var check = NewDb();
        Assert.False(await check.AgentTasks.AnyAsync(t => t.PendingLessonHarvest));
    }

    // ── Vòng chắt lọc ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Harvest_WritesLessonToTheRoleThatRanTheStage_InCommonBucket()
    {
        await SeedQueuedFeedbackAsync(AgentTaskType.ArchitectureDesign, _techLeadId, "gọi API tỉ giá mà không có phương án khi nó chết");
        var llm = new FakeLlm { Reply = OneLesson };

        await using (var db = NewDb())
            await NewSut(db, llm).TryHarvestAsync(_projectId);

        Assert.Equal(1, llm.Calls);
        Assert.Contains("gọi API tỉ giá", llm.LastUserMessage);
        Assert.Contains("Technical Lead", llm.LastUserMessage); // vai đi kèm để prompt biết đang rút cho ai.

        await using var check = NewDb();
        var item = await check.AgentChecklistItems.SingleAsync();
        Assert.Equal(_techLeadId, item.AgentId);           // vào ĐÚNG vai đã chạy bước.
        Assert.Null(item.DepartmentCode);                  // bucket CHUNG — bài học kỹ thuật không chia phòng ban.
        Assert.Equal(ChecklistItemSource.StageRevision, item.SourceKind);
        Assert.Equal(_projectId, item.SourceProjectId);    // truy nguồn về dự án đã sinh ra bài học.
        Assert.StartsWith("Mọi lời gọi ra dịch vụ ngoài", item.Text);
        Assert.False(await check.AgentTasks.AnyAsync(t => t.PendingLessonHarvest)); // hàng đợi đã dọn.
    }

    // Hai vai cùng bị góp ý trong một dự án ⇒ hai lời gọi riêng, hai checklist riêng. Gộp chung sẽ dạy
    // Developer những điều chỉ đúng với Technical Lead.
    [Fact]
    public async Task Harvest_SplitsPerRole()
    {
        await SeedQueuedFeedbackAsync(AgentTaskType.ArchitectureDesign, _techLeadId, "thiếu phương án dự phòng");
        await SeedQueuedFeedbackAsync(AgentTaskType.Implementation, _developerId, "thiếu unit test cho service");
        var llm = new FakeLlm { Reply = OneLesson };

        await using (var db = NewDb())
            await NewSut(db, llm).TryHarvestAsync(_projectId);

        Assert.Equal(2, llm.Calls);
        await using var check = NewDb();
        Assert.Equal(1, await check.AgentChecklistItems.CountAsync(x => x.AgentId == _techLeadId));
        Assert.Equal(1, await check.AgentChecklistItems.CountAsync(x => x.AgentId == _developerId));
    }

    // Bài học chỉ được ĐỌC LẠI ở đường agent chung (AgentRunService). Bước TechnicalDocs do BA chạy qua
    // RequirementDocsService nên không đi qua đó — học ở đấy là trả tiền cho thứ không ai đọc.
    [Fact]
    public async Task Harvest_SkipsTechnicalDocs_WithoutCallingLlm()
    {
        var baId = await NewDb().Agents.Where(a => a.RoleKey == AgentRoleKey.BusinessAnalyst).Select(a => a.Id).SingleAsync();
        await SeedQueuedFeedbackAsync(AgentTaskType.TechnicalDocs, baId, "phần SRS thiếu mục phi chức năng");
        var llm = new FakeLlm { Reply = OneLesson };

        await using (var db = NewDb())
            await NewSut(db, llm).TryHarvestAsync(_projectId);

        Assert.Equal(0, llm.Calls);
        await using var check = NewDb();
        Assert.Empty(await check.AgentChecklistItems.ToListAsync());
        Assert.False(await check.AgentTasks.AnyAsync(t => t.PendingLessonHarvest)); // vẫn dọn hàng đợi.
    }

    [Fact]
    public async Task Harvest_EmptyQueue_DoesNotCallLlm()
    {
        var llm = new FakeLlm();

        await using (var db = NewDb())
            await NewSut(db, llm).TryHarvestAsync(_projectId);

        Assert.Equal(0, llm.Calls);
    }

    // Fail-open: lời gọi lỗi ⇒ giữ nguyên hàng đợi để lần drain sau gộp bù. Dọn hàng đợi ở đây là đánh mất
    // vĩnh viễn nhận xét người dùng đã bỏ công gõ.
    [Fact]
    public async Task Harvest_LlmFails_KeepsQueueForNextDrain()
    {
        await SeedQueuedFeedbackAsync(AgentTaskType.ArchitectureDesign, _techLeadId, "thiếu phương án dự phòng");
        var llm = new FakeLlm { Fail = true };

        await using (var db = NewDb())
            await NewSut(db, llm).TryHarvestAsync(_projectId);

        await using var check = NewDb();
        Assert.Empty(await check.AgentChecklistItems.ToListAsync());
        Assert.True(await check.AgentTasks.AnyAsync(t => t.PendingLessonHarvest));
    }

    // "Không rút được bài học nào" là câu trả lời hợp lệ và thường gặp (phần lớn nhận xét chỉ đúng cho
    // riêng dự án đó). Vẫn phải dọn hàng đợi — lời gọi đã tiêu, gộp lại vòng sau không khá hơn.
    [Fact]
    public async Task Harvest_NoLessonWorthLearning_ClearsQueueWithoutWriting()
    {
        await SeedQueuedFeedbackAsync(AgentTaskType.Implementation, _developerId, "đổi nhãn nút thành 'Ghi nhận'");
        var llm = new FakeLlm { Reply = """{"items":[]}""" };

        await using (var db = NewDb())
            await NewSut(db, llm).TryHarvestAsync(_projectId);

        await using var check = NewDb();
        Assert.Empty(await check.AgentChecklistItems.ToListAsync());
        Assert.False(await check.AgentTasks.AnyAsync(t => t.PendingLessonHarvest));
    }

    // Bài học người dùng đã TẮT phải đi kèm lời gọi làm danh sách cấm — cùng luật với checklist của BA,
    // nếu không thì gỡ một bài học sai chỉ có tác dụng tới lần harvest kế tiếp.
    [Fact]
    public async Task Harvest_SendsDisabledLessonsAsBlocklist()
    {
        await SeedQueuedFeedbackAsync(AgentTaskType.ArchitectureDesign, _techLeadId, "thiếu phương án dự phòng");
        await using (var seed = NewDb())
        {
            seed.AgentChecklistItems.Add(new AgentChecklistItem
            {
                AgentId = _techLeadId,
                Text = "Luôn vẽ sơ đồ tuần tự cho mọi luồng.",
                Status = ChecklistItemStatus.DisabledByUser
            });
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm { Reply = OneLesson };
        await using (var db = NewDb())
            await NewSut(db, llm).TryHarvestAsync(_projectId);

        Assert.Contains("Luôn vẽ sơ đồ tuần tự cho mọi luồng.", llm.LastUserMessage);
    }

    // ── Đường ĐỌC lại: bài học phải vào được system prompt của vai ────────────────────────────────────

    [Fact]
    public void PromptBuilder_InjectsLearnedChecklist()
    {
        var agent = new Agent { RoleKey = AgentRoleKey.TechLead };
        var built = NewPromptBuilder().BuildNative(agent, "- Mọi lời gọi ra dịch vụ ngoài phải có xử lý lỗi.");

        Assert.Contains("Mọi lời gọi ra dịch vụ ngoài phải có xử lý lỗi.", built);
        Assert.Contains("learned from reviewer feedback", built);
    }

    // Vai chưa học được gì ⇒ prompt giữ nguyên như trước: một tiêu đề "Bài học" không có mục nào chỉ dạy
    // model rằng phần đó vô nghĩa.
    [Fact]
    public void PromptBuilder_NoChecklist_LeavesNoEmptyHeading()
    {
        var agent = new Agent { RoleKey = AgentRoleKey.TechLead };
        var built = NewPromptBuilder().BuildNative(agent);

        Assert.DoesNotContain("learned from reviewer feedback", built);
        Assert.DoesNotContain("{{learnedChecklist}}", built);
    }

    // Nút "Checklist học được" chỉ hiện ở vai CÓ đường học; Designer không chạy bước pipeline nào nên một
    // trang checklist cho vai đó sẽ rỗng vĩnh viễn.
    [Fact]
    public void LearnedChecklistRoles_CoversPipelineRolesOnly()
    {
        Assert.Contains(AgentRoleKey.BusinessAnalyst, LearnedChecklistRoles.All);
        Assert.Contains(AgentRoleKey.TechLead, LearnedChecklistRoles.All);
        Assert.Contains(AgentRoleKey.Developer, LearnedChecklistRoles.All);
        Assert.Contains(AgentRoleKey.Tester, LearnedChecklistRoles.All);
        Assert.False(LearnedChecklistRoles.Has(AgentRoleKey.UiUx));
    }

    // ── Hạ tầng test ─────────────────────────────────────────────────────────────────────────────────

    private async Task SeedWaitingAsync(WorkflowStageKey stage)
    {
        await using var db = NewDb();
        db.WorkflowRuns.Add(new WorkflowRun
        {
            Id = _runId,
            ProjectId = _projectId,
            Status = WorkflowRunStatus.WaitingForHuman,
            CurrentStage = stage
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> AddTaskAsync(AgentTaskType type, Guid agentId, string? feedback, bool queued = false)
    {
        await using var db = NewDb();
        var task = new AgentTask
        {
            WorkflowRunId = _runId,
            ProjectId = _projectId,
            AgentId = agentId,
            Type = type,
            Status = AgentTaskStatus.Completed,
            Title = type.ToString(),
            RevisionFeedback = feedback,
            PendingLessonHarvest = queued
        };
        db.AgentTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    // Bỏ qua cổng duyệt: dựng thẳng một task ĐÃ nằm trong hàng đợi, để test vòng chắt lọc tách khỏi test
    // cổng duyệt (hai thứ hỏng vì lý do khác nhau).
    private async Task SeedQueuedFeedbackAsync(AgentTaskType type, Guid agentId, string feedback)
    {
        if (!await NewDb().WorkflowRuns.AnyAsync(r => r.Id == _runId))
            await SeedWaitingAsync(WorkflowStageKey.ArchitectureDesign);

        await AddTaskAsync(type, agentId, feedback, queued: true);
    }

    private ApproveStageUseCase NewApprove(AppDbContext db) => new(db, new ProjectArtifactCatalog());

    private StageRevisionMemoryService NewSut(AppDbContext db, ILlmClient llm) =>
        new(db, llm, new StubPrompts(), new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            NullLogger<StageRevisionMemoryService>.Instance);

    private static AgentPromptBuilder NewPromptBuilder()
    {
        var prompts = new StubPrompts();
        return new AgentPromptBuilder(prompts, new AgentInstructionProvider(prompts));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public string Reply = """{"items":[]}""";
        public bool Fail;
        public string LastUserMessage = string.Empty;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
            return Task.FromResult(new LlmCallResult
            {
                IsSuccess = !Fail,
                Content = Fail ? string.Empty : Reply,
                ErrorMessage = Fail ? "boom" : null
            });
        }

        public async Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => (await ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken), null);
    }

    // Trả về CHÍNH template thật cho tool-agent-native (test prompt builder cần placeholder thật), còn lại
    // là chuỗi giữ chỗ — vòng harvest không quan tâm nội dung system prompt.
    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }

        public override string Get(string relativePath) => relativePath switch
        {
            "Shared/tool-agent-native.v1.md" => "You are the {{roleTitle}} agent.\n\nInstruction:\n{{instruction}}\n{{learnedChecklist}}\n\nRules: ...",
            _ => "## rút kinh nghiệm"
        };
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
