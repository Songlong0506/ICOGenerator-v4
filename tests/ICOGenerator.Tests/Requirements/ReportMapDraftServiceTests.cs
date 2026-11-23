using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// LƯỢT RÁP BẢNG BÁO CÁO — bảng báo cáo không còn bày ra cho người dùng chốt: nhóm «Báo cáo / thống kê» lên
// [RÕ] bằng chính lựa chọn của họ (chip BA đề xuất), nên bày thêm một bảng là hỏi lại điều vừa được trả lời,
// rồi hỏi lần thứ ba ở bảng màn hình. Service này ráp bảng ngầm và gieo các báo cáo vào bảng màn hình ở trạng
// thái CHỜ DUYỆT — một màn tổng quan gom chung, cộng màn riêng của báo cáo nào cần — chỗ duy nhất người dùng
// rà chúng.
//
// Thứ phải giữ: (1) ráp xong thì cả bảng lẫn các màn hình gieo ra đều được lưu trong MỘT lần; (2) chỉ ráp
// một lần; (3) nhóm chưa [RÕ] thì không gọi model; (4) lời gọi hỏng thì không ghi gì, để lượt kế ráp lại.
public class ReportMapDraftServiceTests : IDisposable
{
    private static string Coverage(string reports) => CoverageMapFixture.Map($"""
        - ★ Mục tiêu / bài toán: [RÕ] Quản lý tủ locker.
        - ★ Đối tượng người dùng & vai trò: [RÕ] Nhân viên xin tủ, FCM cấp tủ.
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Xin tủ, duyệt, cấp tủ.
        - Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] Tủ hư thì chuyển damaged.
        - Dữ liệu / danh mục chính: [RÕ] Tủ, khu, tòa.
        - Vòng đời & trạng thái: [RÕ] Available → In use → Damaged.
        - Báo cáo / thống kê: {reports}
        - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
        """);

    private static readonly List<ReportMapRow> Drafted = new()
    {
        new() { Report = "Locker Status Report", Question = "để FCM nắm tình trạng chung của locker", Source = "Locker", Breakdown = "khu; trạng thái" },
        new() { Report = "Department Locker", Question = "để biết bộ phận nào đang giữ nhiều tủ", Source = "Locker", Breakdown = "bộ phận", OwnScreen = true }
    };

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Agent _ba;
    private readonly Guid _projectId = Guid.NewGuid();

    public ReportMapDraftServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _ba = new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = _model.Id };

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(_ba);
        db.SaveChanges();
    }

    private void SeedProject(string reportsStatus)
    {
        using var db = NewDb();
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Locker",
            Description = "quản lý tủ locker",
            RequirementCoverageMap = Coverage(reportsStatus)
        });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId, AgentId = _ba.Id, Role = "user",
            Message = "Số tủ theo từng trạng thái theo khu, Số tủ mỗi bộ phận đang giữ"
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task DraftsTheTable_AndEveryReportBecomesATaskOfTheDesign()
    {
        SeedProject("[RÕ] Số tủ theo trạng thái; số tủ mỗi bộ phận.");
        var llm = new FakeLlm { Reports = Drafted };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);
        var saved = await new ReportMapDraftService(db, llm, new StubPrompts()).UpdateAsync(project, _ba, _model);

        Assert.Equal(2, saved);

        await using var fresh = NewDb();
        var stored = await fresh.Projects.AsNoTracking().FirstAsync(p => p.Id == _projectId);
        Assert.Equal(2, ReportMapBuilder.Parse(stored.ReportMap).Count);

        // Mỗi báo cáo là một tác vụ của bản thiết kế UX (gieo tất định lúc thiết kế), kèm chỗ đứng bảng này ghi:
        // màn tổng quan (tên do model đặt) cho báo cáo thường, màn riêng gắn hậu tố cho báo cáo cần màn riêng.
        // Không có đường này thì báo cáo không có dòng phân quyền nào.
        Assert.Equal(2, UxInventoryBuilder.ReportTasks(stored.ReportMap).Count);
        Assert.Equal(new[] { "Locker Dashboard", "Department Locker Report" }, ReportMapBuilder.ReportScreens(stored.ReportMap));

        // Cổng đóng lại ⇒ lượt kế không ráp lần nữa, và các bảng đứng sau được phép bày.
        Assert.False(ReportMapGate.ShouldDraft(stored));
    }

    // Lời người dùng phải tới được model: bảng chỉ được ráp từ điều họ đã chọn / đã kể.
    [Fact]
    public async Task TheModelReadsTheConversation_AndWhatTheCoverageMapRecorded()
    {
        SeedProject("[RÕ] Số tủ theo trạng thái; số tủ mỗi bộ phận.");
        var llm = new FakeLlm { Reports = Drafted };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);
        await new ReportMapDraftService(db, llm, new StubPrompts()).UpdateAsync(project, _ba, _model);

        Assert.Contains("Số tủ mỗi bộ phận đang giữ", llm.LastUserMessage, StringComparison.Ordinal);
        Assert.Contains("Số tủ theo trạng thái", llm.LastUserMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[MỘT PHẦN] Cần có báo cáo tổng hợp.")]
    [InlineData("[KHÔNG ÁP DỤNG] Không cần báo cáo.")]
    public async Task DoesNotCallTheModel_UntilTheGroupIsClear(string reportsStatus)
    {
        SeedProject(reportsStatus);
        var llm = new FakeLlm { Reports = Drafted };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);
        var saved = await new ReportMapDraftService(db, llm, new StubPrompts()).UpdateAsync(project, _ba, _model);

        Assert.Equal(0, saved);
        Assert.Equal(0, llm.Calls);
        Assert.Null(project.ReportMap);
    }

    [Fact]
    public async Task DraftsOnlyOnce()
    {
        SeedProject("[RÕ] Số tủ theo trạng thái.");
        var llm = new FakeLlm { Reports = Drafted };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);
        var sut = new ReportMapDraftService(db, llm, new StubPrompts());
        await sut.UpdateAsync(project, _ba, _model);
        await sut.UpdateAsync(project, _ba, _model);

        Assert.Equal(1, llm.Calls);
    }

    // Fail-open: lời gọi hỏng (hoặc không dòng nào dùng được) thì KHÔNG ghi gì — cổng còn mở nên lượt kế ráp
    // lại, và InterviewTableGate.Select giữ các bảng đứng sau lại cho tới lúc đó.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedOrEmptyDraft_LeavesTheGateOpen(bool callSucceeds)
    {
        SeedProject("[RÕ] Số tủ theo trạng thái.");
        var llm = new FakeLlm { Succeeds = callSucceeds };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);
        var saved = await new ReportMapDraftService(db, llm, new StubPrompts()).UpdateAsync(project, _ba, _model);

        Assert.Equal(0, saved);
        Assert.Null(project.ReportMap);
        Assert.True(ReportMapGate.ShouldDraft(project));
        Assert.Equal(InterviewTableKind.FlowMap, InterviewTableGate.Select(project));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        public List<ReportMapRow> Reports = new();
        public bool Succeeds = true;
        public int Calls;
        public string LastUserMessage = "";

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used in this test");

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            Assert.Equal("BAReportMapDraft", logContext.Purpose);
            Calls++;
            LastUserMessage = messages.Last(m => m.Role == ChatRole.User).Text ?? "";

            return Task.FromResult(Succeeds
                ? (new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)(object)new ReportMapDraft { Dashboard = "Locker Dashboard", Reports = Reports })
                : (new LlmCallResult { IsSuccess = false, ErrorMessage = "boom" }, (T?)null));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }
}
