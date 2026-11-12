using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Organization;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// NGÂN SÁCH LƯỢT CỦA BUỔI PHỎNG VẤN — và khối "## CHẶNG VỀ ĐÍCH" nó bật lên.
//
// Ca thật (dự án Locker management 7): 46 lượt người dùng trong hơn 70 phút, 7/12 nhóm vẫn chưa [RÕ],
// chưa bảng chốt nào được bày, trong khi người dùng muốn cả buổi gói trong một giờ. Prompt dạy BA gộp câu
// hỏi và đi hết từng chủ đề, nhưng không luật nào trong đó biết buổi phỏng vấn đã dài bao lâu — model
// không đếm được lượt. Con số phải đến từ code, và các bất biến dưới đây giữ cho nó:
//   - chỉ bật khi ĐÃ quá ngân sách (bật sớm là giục BA đóng nhóm ngay từ đầu buổi);
//   - gọi đúng tên các nhóm còn hở, và KHÔNG lôi hai nhóm chốt-bằng-bảng vào (BA bị cấm hỏi lẻ chúng);
//   - im lặng khi không còn gì để đóng bằng câu hỏi.
public class InterviewPaceBudgetTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    // Bản đồ của một buổi đang dở: hai nhóm thường còn hở, cộng hai nhóm chốt-bằng-bảng chưa tới lượt.
    private static readonly string OpenMap = CoverageMapFixture.DistillReply(
        "- ★ Mục tiêu / bài toán: [MỘT PHẦN] app quản lý locker trong nhà máy.\n"
        + "- ★ Chức năng & luồng nghiệp vụ chính: [RÕ] nhân viên gửi yêu cầu, FCM duyệt rồi cấp tủ.\n"
        + "- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] tủ tạm thời phải nhập ngày from, to.\n"
        + "- Thông báo / nhắc nhở: [MỘT PHẦN] người dùng tủ được báo khi sửa xong.\n"
        + "- Phân quyền theo nghiệp vụ: [CHƯA HỎI]");

    public InterviewPaceBudgetTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "app quản lý locker" });
        db.SaveChanges();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(InterviewPaceBudget.ClosingAfterUserTurns - 1, false)]
    [InlineData(InterviewPaceBudget.ClosingAfterUserTurns, true)]
    [InlineData(InterviewPaceBudget.ClosingAfterUserTurns + 20, true)]
    public void IsClosing_FlipsExactlyAtTheBudget(int userTurns, bool expected)
        => Assert.Equal(expected, InterviewPaceBudget.IsClosing(userTurns));

    [Fact]
    public void GroupsToClose_ListsOpenGroups_ButNotClearOnesNorTheOnesWaitingForATable()
    {
        var map = CoverageMapFixture.Map(
            "- ★ Mục tiêu / bài toán: [MỘT PHẦN] app quản lý locker.\n"
            + "- ★ Chức năng & luồng nghiệp vụ chính: [RÕ] nhân viên gửi yêu cầu.\n"
            + "- Báo cáo / thống kê: [KHÔNG ÁP DỤNG]\n"
            + "- Quy mô sử dụng: [CHƯA HỎI]\n"
            + "- Thông báo / nhắc nhở: [MỘT PHẦN] báo khi sửa xong.\n"
            + "- Phân quyền theo nghiệp vụ: [CHƯA HỎI]");

        var groups = InterviewPaceBudget.GroupsToClose(map, new[]
        {
            PermissionMatrixGate.PermissionGroupLabel, NotificationMapGate.NotificationGroupLabel
        });

        Assert.Equal(new[] { "Mục tiêu / bài toán", "Quy mô sử dụng" }, groups);
    }

    [Fact]
    public async Task UnderBudget_TheClosingBlockIsNotAttached()
    {
        await SeedAnsweredTurnsAsync(InterviewPaceBudget.ClosingAfterUserTurns - 2);
        var llm = new FakeLlm(OpenMap);

        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "FCM duyệt rồi cấp tủ");

        Assert.DoesNotContain(llm.LastChatSystemMessages,
            m => m.Contains(BAChatPromptBlocks.InterviewClosingHeading, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OverBudget_TheClosingBlockNamesTheOpenGroups_AndLeavesTheTableGroupsOut()
    {
        // Lượt người dùng sắp gửi là lượt chạm đúng ngân sách.
        await SeedAnsweredTurnsAsync(InterviewPaceBudget.ClosingAfterUserTurns - 1);
        var llm = new FakeLlm(OpenMap);

        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "FCM duyệt rồi cấp tủ");

        var block = Assert.Single(llm.LastChatSystemMessages,
            m => m.StartsWith(BAChatPromptBlocks.InterviewClosingHeading, StringComparison.Ordinal));
        Assert.Contains("«Mục tiêu / bài toán»", block, StringComparison.Ordinal);
        Assert.Contains("«Quy tắc nghiệp vụ & ràng buộc»", block, StringComparison.Ordinal);
        // Hai nhóm chốt-bằng-bảng không được nêu tên là nhóm phải đóng bằng câu hỏi: khối "ĐỂ CUỐI, đừng
        // hỏi lẻ" của chúng vẫn đang nằm trong cùng ngữ cảnh.
        Assert.DoesNotContain("«Phân quyền theo nghiệp vụ»", block, StringComparison.Ordinal);
        Assert.DoesNotContain("«Thông báo / nhắc nhở»", block, StringComparison.Ordinal);
        Assert.Contains(llm.LastChatSystemMessages,
            m => m.StartsWith(BAChatPromptBlocks.PermissionMatrixDeferred[..40], StringComparison.Ordinal));
        // Con số người dùng đã trả lời đi kèm, đếm cả lượt vừa gửi.
        Assert.Contains($"đã trả lời {InterviewPaceBudget.ClosingAfterUserTurns} lượt", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverBudget_ButOnlyTableGroupsAreLeft_TheClosingBlockStaysSilent()
    {
        await SeedAnsweredTurnsAsync(InterviewPaceBudget.ClosingAfterUserTurns + 5);
        // Mọi nhóm thường đã đóng — chỉ còn hai nhóm đang chờ bảng. Bản đồ không có dòng «Chức năng &
        // luồng nghiệp vụ chính» nên không cổng bảng nào mở ở lượt này: đây vẫn là một lượt chat thường.
        var llm = new FakeLlm(CoverageMapFixture.DistillReply(
            "- ★ Mục tiêu / bài toán: [RÕ] app quản lý locker trong nhà máy.\n"
            + "- Thông báo / nhắc nhở: [MỘT PHẦN] người dùng tủ được báo khi sửa xong.\n"
            + "- Phân quyền theo nghiệp vụ: [CHƯA HỎI]"));

        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "FCM duyệt rồi cấp tủ");

        Assert.DoesNotContain(llm.LastChatSystemMessages,
            m => m.Contains(BAChatPromptBlocks.InterviewClosingHeading, StringComparison.Ordinal));
    }

    // Khối phải giữ ĐÚNG ranh giới với cổng "chốt nhanh phần còn lại" đã bỏ: BA vẫn hỏi, người dùng chốt
    // từng câu. Mất một trong các câu dưới là khối trượt về đúng cái cổng đó.
    [Fact]
    public void ClosingBlock_KeepsTheUserAsTheOneWhoAnswers()
    {
        var block = BAChatPromptBlocks.InterviewClosing(30, new[] { "Mục tiêu / bài toán" });

        Assert.StartsWith(BAChatPromptBlocks.InterviewClosingHeading, block, StringComparison.Ordinal);
        Assert.Contains("Người dùng chốt TỪNG câu", block, StringComparison.Ordinal);
        Assert.Contains("tối đa 4 câu", block, StringComparison.Ordinal);
        Assert.Contains("KHÔNG mở chủ đề mới", block, StringComparison.Ordinal);
        Assert.Contains("đừng bịa câu hỏi", block, StringComparison.Ordinal);
        Assert.Contains("ĐỂ CUỐI, đừng hỏi lẻ", block, StringComparison.Ordinal);
        // Không đổi điều kiện mở cổng "Write Requirement".
        Assert.Contains("`ready` vẫn chỉ `true`", block, StringComparison.Ordinal);
    }

    // n cặp (user trả lời, BA hỏi) đã diễn ra — lượt cuối là của BA, như một hội thoại đang chờ người dùng.
    private async Task SeedAnsweredTurnsAsync(int userTurns)
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < userTurns; i++)
        {
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = _baId,
                Role = "user",
                Message = $"Câu trả lời số {i + 1} của người dùng",
                CreatedAt = baseTime.AddSeconds(2 * i)
            });
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = _baId,
                Role = "assistant",
                Message = $"Câu hỏi khai thác thứ {i + 1} về một góc riêng của bài toán số {i + 1}?",
                CreatedAt = baseTime.AddSeconds(2 * i + 1)
            });
        }
        await db.SaveChangesAsync();
    }

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
            new RequirementCoverageService(db, llm, prompts, new CoverageChecklist(prompts)),
            new OrganizationContextService(db, prompts,
                new OrgChartProvider(db, new MemoryCache(new MemoryCacheOptions())),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new InterviewScopeService(db, llm, prompts),
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Chỉ hai lời gọi có mặt ở đây: lượt chắt bản đồ (trả bản đồ cố định) và lượt chat (ghi lại các khối
    // system đã gửi). Mọi lời gọi khác — tóm tắt hội thoại, hồ sơ người dùng — báo lỗi để service đi
    // đường fail-open của nó.
    private sealed class FakeLlm : ILlmClient
    {
        private readonly string _coverageMap;

        public FakeLlm(string coverageMap) => _coverageMap = coverageMap;

        public List<string> LastChatSystemMessages = new();

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(logContext.Purpose == "BARequirementCoverage"
                ? new LlmCallResult { IsSuccess = true, Content = _coverageMap }
                : new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            if (logContext.Purpose != "BAChat")
                return Task.FromResult((ChatWithLogAsync(model, messages, logContext, onToken, cancellationToken).Result, (T?)null));

            LastChatSystemMessages = messages
                .Where(m => m.Role == ChatRole.System)
                .Select(m => m.Text ?? string.Empty)
                .ToList();

            var reply = new BAChatReply
            {
                Message = "Ứng dụng này giúp FCM bớt được việc gì so với file Excel hiện nay?",
                OpenEnded = true
            };
            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)(object)reply));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }
}
