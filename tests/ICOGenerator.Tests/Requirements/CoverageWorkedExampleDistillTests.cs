using ICOGenerator.Contracts.Requirements;
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

// ĐƯỜNG GHI của "ví dụ đã xác nhận" (Project.WorkedExamples) — nay là CỘT THỨ BA của lượt chắt lọc bản đồ
// bao phủ, không còn một lời gọi LLM riêng chạy ở hậu kỳ lượt chat. Các test chốt bốn điều mà việc gộp ấy
// đứng hoặc ngã theo: (1) danh sách được ghi cùng lượt, lưu JSON; (2) model KHÔNG trả trường ⇒ giữ nguyên
// cột (null ≠ mảng rỗng — nếu không thì một lượt distill lơ đãng xoá trắng oracle chấm POC); (3) mảng RỖNG
// là câu trả lời hợp lệ và có quyền xoá (người dùng bác ví dụ cuối cùng); (4) CoverageWorkedExampleGuard
// chấm bằng danh sách của CHÍNH lượt này, không phải bản cũ một lượt.
public class CoverageWorkedExampleDistillTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    public CoverageWorkedExampleDistillTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.SaveChanges();
    }

    // Cột lưu JSON, không phải bullet: chốt luôn ở đây để một lần đổi format nữa không lặng lẽ đi qua.
    [Fact]
    public async Task WorkedExamples_AreWrittenByTheCoverageDistill_AsJson()
    {
        var (project, ba) = await SeedAsync(turns: 2);
        var llm = NewLlm(workedExamples: new List<string> { "23 người, sĩ số 8–12 ⇒ mở 2 lớp" });

        await RunAsync(project, ba, llm);

        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.StartsWith("{", reloaded.WorkedExamples!.TrimStart(), StringComparison.Ordinal);
        Assert.Equal("23 người, sĩ số 8–12 ⇒ mở 2 lớp",
            InterviewOutlookParser.ParseWorkedExamples(reloaded.WorkedExamples).Single());
    }

    // NULL ≠ mảng rỗng. Trường vắng mặt là model quên, không phải model nói "không còn ví dụ nào" — mà cột
    // này là oracle POC bị chấm theo, nên mất nó là mất trong im lặng. Ca thật của đường parse tay: một
    // model không nhận response_format trả về JSON chỉ có items + questions.
    [Fact]
    public async Task MissingField_KeepsTheStoredList()
    {
        var (project, ba) = await SeedAsync(turns: 2,
            existingExamples: InterviewOutlookParser.SerializeWorkedExamples(new[] { "ví dụ cũ" }));

        await RunAsync(project, ba, NewLlm(workedExamples: null));

        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal("ví dụ cũ", InterviewOutlookParser.ParseWorkedExamples(reloaded.WorkedExamples).Single());
    }

    // Mảng RỖNG thì ngược lại: đó là một câu trả lời hợp lệ ("ví dụ cuối cùng vừa bị người dùng bác"), nên
    // nó được quyền xoá. Không có nhánh này thì một ví dụ đã bị bác nằm lại vĩnh viễn và chảy tiếp vào
    // "## 13. Worked Examples".
    [Fact]
    public async Task EmptyArray_ClearsTheStoredList()
    {
        var (project, ba) = await SeedAsync(turns: 2,
            existingExamples: InterviewOutlookParser.SerializeWorkedExamples(new[] { "ví dụ vừa bị bác" }));

        await RunAsync(project, ba, NewLlm(workedExamples: new List<string>()));

        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Empty(InterviewOutlookParser.ParseWorkedExamples(reloaded.WorkedExamples));
    }

    // Khối "ví dụ hiện có" echo lại cho chính lượt chắt lọc là bullet, không phải JSON — nhét dấu ngoặc
    // nhọn vào prompt vừa tốn token vừa mời model chép cú pháp ấy ra chỗ khác.
    [Fact]
    public async Task TheEchoedState_IsBulletsNotJson()
    {
        var (project, ba) = await SeedAsync(turns: 2,
            existingExamples: InterviewOutlookParser.SerializeWorkedExamples(new[] { "23 người ⇒ mở 2 lớp" }));
        var llm = NewLlm(workedExamples: new List<string>());

        await RunAsync(project, ba, llm);

        Assert.Contains("- 23 người ⇒ mở 2 lớp", llm.LastUserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("\"items\":[{\"label\"", llm.LastUserMessage, StringComparison.Ordinal);
    }

    // Khối ấy in ra CẢ KHI danh sách rỗng — khác hai khối "bản đồ hiện có" / "câu hỏi hiện có", vốn biến
    // mất khi chưa có gì. Rỗng là trạng thái BÌNH THƯỜNG suốt nửa đầu buổi phỏng vấn, và một trường vắng
    // mặt trong đầu vào là trường model dễ bỏ quên luôn trong đầu ra — mà bỏ quên thì danh sách đứng im,
    // tức không ai gỡ nổi một ví dụ vừa bị người dùng bác.
    [Fact]
    public async Task TheEchoedState_PrintsEvenWhenEmpty()
    {
        var (project, ba) = await SeedAsync(turns: 2);
        var llm = NewLlm(workedExamples: new List<string>());

        await RunAsync(project, ba, llm);

        Assert.Contains("Ví dụ đã xác nhận hiện có", llm.LastUserMessage, StringComparison.Ordinal);
        Assert.Contains("(chưa có)", llm.LastUserMessage, StringComparison.Ordinal);
    }

    // Đây là thứ việc gộp hai lời gọi làm một MUA được: guard đọc danh sách của chính lượt này. Khi danh
    // sách còn do một lời gọi hậu kỳ chắt ra, dòng «Quy tắc nghiệp vụ» chở con số vẫn bị hạ ở lượt người
    // dùng vừa chốt ví dụ — guard chấm bằng bản cũ đúng một lượt.
    [Fact]
    public async Task TheGuard_SeesThisTurnsExamples_NotThePreviousOnes()
    {
        var (project, ba) = await SeedAsync(turns: 2);
        var llm = NewLlm(
            map: "- Quy tắc nghiệp vụ & ràng buộc: [RÕ] Khóa hiệu lực 1 năm, quá hạn thì gửi mail.",
            workedExamples: new List<string> { "Học xong 1/3/2025 → hạn 1/3/2026; chưa học lại ⇒ «Quá hạn» + gửi mail." });

        var coverage = await RunAsync(project, ba, llm);

        var rule = CoverageMapParser.Parse(coverage.Map)
            .First(x => x.Label.StartsWith("Quy tắc nghiệp vụ", StringComparison.Ordinal));
        Assert.Equal("RÕ", rule.Status);
        Assert.DoesNotContain(coverage.Questions, q => q.Text == CoverageWorkedExampleGuard.MissingExampleQuestion);
    }

    // Mặt còn lại của cùng một chốt chặn: không có ví dụ nào thì dòng chở con số vẫn bị hạ, kèm câu xin ví
    // dụ. Guard không còn ĐỘC LẬP (cùng lời gọi viết cả hai) nhưng ca model quên hẳn ví dụ — ca thường gặp
    // nhất — vẫn phải bắt được.
    [Fact]
    public async Task NoExamples_StillDowngradesTheNumericRuleRow()
    {
        var (project, ba) = await SeedAsync(turns: 2);
        var llm = NewLlm(
            map: "- Quy tắc nghiệp vụ & ràng buộc: [RÕ] Khóa hiệu lực 1 năm, quá hạn thì gửi mail.",
            workedExamples: new List<string>());

        var coverage = await RunAsync(project, ba, llm);

        var rule = CoverageMapParser.Parse(coverage.Map)
            .First(x => x.Label.StartsWith("Quy tắc nghiệp vụ", StringComparison.Ordinal));
        Assert.Equal("MỘT PHẦN", rule.Status);
        Assert.Contains(coverage.Questions, q => q.Text == CoverageWorkedExampleGuard.MissingExampleQuestion);
    }

    // VÒNG LẶP KÍN của ca thật (dự án quản lý khóa học bắt buộc, 2026-09-05), chốt ở tầng service vì nó chỉ
    // hiện ra khi cả chuỗi guard chạy cùng nhau.
    //
    // Ví dụ đã nằm trong workedExamples từ nhiều lượt trước, nhưng câu hỏi mà CoverageWorkedExampleGuard
    // đặt xuống hồi danh sách còn rỗng vẫn được distiller chép sang lượt này ở trạng thái MỞ — nó chỉ thấy
    // CÁC LƯỢT MỚI, mà lượt người dùng gật ví dụ đã trôi khỏi cửa sổ đó từ lâu, nên luật ảnh-chụp-lũy-tiến
    // bảo nó chép lại. Nếu guard chỉ `return` khi danh sách hết rỗng thì không ai gỡ mục ấy:
    // CoveragePendingGuard hạ dòng «Quy tắc nghiệp vụ» xuống [MỘT PHẦN] và nút "Write Requirement" khóa
    // vĩnh viễn, còn cổng readiness phát lại chính câu ấy mỗi lượt.
    [Fact]
    public async Task AStaleExampleQuestion_IsReleased_OnceTheListIsNoLongerEmpty()
    {
        var (project, ba) = await SeedAsync(turns: 2,
            existingExamples: InterviewOutlookParser.SerializeWorkedExamples(new[] { "Hết hạn 30/6 ⇒ gửi mail từ 1/6, mỗi tuần một lần." }));

        var llm = NewLlm(
            map: "- Quy tắc nghiệp vụ & ràng buộc: [RÕ] Nhắc trước 30 ngày và lặp lại hàng tuần.",
            workedExamples: new List<string> { "Hết hạn 30/6 ⇒ gửi mail từ 1/6, mỗi tuần một lần." },
            questions: new List<OpenQuestionEntry>
            {
                new()
                {
                    Group = "Quy tắc nghiệp vụ & ràng buộc",
                    Text = CoverageWorkedExampleGuard.MissingExampleQuestion,
                    Status = OpenQuestionEntry.Open
                }
            });

        var coverage = await RunAsync(project, ba, llm);

        Assert.DoesNotContain(coverage.Questions, q => q.Text == CoverageWorkedExampleGuard.MissingExampleQuestion);

        // Và vì nhóm không còn câu MỞ nào, CoveragePendingGuard để dòng đứng [RÕ] — đúng chỗ vòng lặp bị
        // cắt: cổng "Write Requirement" mở lại được thay vì khóa mãi.
        var rule = CoverageMapParser.Parse(coverage.Map)
            .First(x => x.Label.StartsWith("Quy tắc nghiệp vụ", StringComparison.Ordinal));
        Assert.Equal("RÕ", rule.Status);

        // Cột lưu cũng phải sạch: ngữ cảnh chat của lượt sau đọc thẳng từ đây.
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.DoesNotContain(CoverageWorkedExampleGuard.MissingExampleQuestion, reloaded.OpenQuestions ?? string.Empty, StringComparison.Ordinal);
    }

    private async Task<RequirementCoverageService.CoverageUpdate> RunAsync(Project project, Agent ba, FakeLlm llm)
    {
        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);
        var prompts = new StubPrompts();
        var sut = new RequirementCoverageService(db, llm, prompts, new CoverageChecklist(prompts));
        return await sut.UpdateAndLoadAsync(trackedProject, trackedBa, _model);
    }

    private static FakeLlm NewLlm(
        List<string>? workedExamples,
        string map = "- ★ Mục tiêu / bài toán: [RÕ] App quản lý khóa học bắt buộc.",
        List<OpenQuestionEntry>? questions = null)
        => new()
        {
            Structured = new CoverageDistillDocument
            {
                Items = CoverageMapParser.Parse(CoverageMapFixture.Map(map))
                    .Select(x => new CoverageMapEntry
                    {
                        Label = x.Label, Core = x.IsCore, Status = x.Status, Known = x.Known.ToList()
                    }).ToList(),
                Questions = questions ?? new List<OpenQuestionEntry>(),
                WorkedExamples = workedExamples
            }
        };

    private async Task<(Project Project, Agent Ba)> SeedAsync(int turns, string? existingExamples = null)
    {
        var ba = new Agent { Id = Guid.NewGuid(), Temperature = 0.2, AiModelId = _model.Id };
        var project = new Project { Id = Guid.NewGuid(), Name = "P", WorkedExamples = existingExamples };

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

    // Đi đường structured output THẬT (Structured != null): đó là đường lượt distill thật đi khi model
    // nhận response_format, và là đường duy nhất phân biệt được "trường vắng mặt" với "mảng rỗng".
    private sealed class FakeLlm : ILlmClient
    {
        public CoverageDistillDocument? Structured;
        public string? LastUserMessage;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            LastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            return Task.FromResult(new LlmCallResult { IsSuccess = true, Content = string.Empty });
        }

        public async Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => (await ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken), Structured as T);
    }

    // Prompt THẬT từ đĩa: CoverageChecklist bóc 12 nhãn nhóm ra từ chính file này, và guard so nhãn theo
    // chúng — một stub trả chuỗi giả làm mọi assert về nhóm mất nghĩa.
    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }

        public override string Get(string relativePath) => CoveragePromptFixture.Read();
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
