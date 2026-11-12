using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// LƯỢT TRỌNG TÀI: một lời gọi LLM ngắn phán các câu hỏi về NGHĨA mà trước đây code tự trả lời bằng bảng
// cụm từ tiếng Việt (hỏi lại, chip rỗng, câu hỏi tồn đọng đã chết, người dùng nhắc tới file).
//
// File này ghim GIAO ƯỚC của lớp InterviewJudge với phần còn lại: nó gửi đi cái gì, đọc về thế nào, và —
// quan trọng nhất — nó KHÔNG BAO GIỜ làm hỏng lượt chat: lỗi nào cũng thành một phán quyết rỗng, và phán
// quyết rỗng thì không xoá được gì. Việc THI HÀNH phán quyết được ghim ở test của từng chốt chặn
// (BAChatRepeatedQuestionTests, BAChatSourceRequestTurnTests, CoverageQuestionGuardTests,
// RequirementCoverageServiceTests).
public class InterviewJudgeTests
{
    private static readonly Agent Ba = new() { Id = Guid.NewGuid() };
    private static readonly AiModel Model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    private static InterviewJudge.TurnQuestion Q(string text, params string[] chips) => new(text, chips);

    [Fact]
    public async Task JudgeTurn_SendsTheLedgerTheUserTurnAndEveryQuestionWithItsChips()
    {
        var llm = new FakeLlm { Structured = new TurnJudgeVerdict { Repeats = { 2 } } };

        var verdict = await new InterviewJudge(llm, new StubPrompts()).JudgeTurnAsync(
            Guid.NewGuid(),
            new[] { "Who will use the app?", "How are reminders sent?" },
            "Security staff use the dashboard",
            new[] { Q("How many employees are there?", "Under 500", "Other"), Q("Who uses the app?") },
            summaryCheck: true, Ba, Model);

        Assert.Equal(new[] { 2 }, verdict.Repeats);
        Assert.Equal(InterviewJudge.TurnPurpose, llm.LastPurpose);
        Assert.Equal(InterviewJudge.TurnPromptPath, llm.LastSystemPrompt);

        var input = llm.LastUserMessage!;
        Assert.Contains("1. Who will use the app?", input, StringComparison.Ordinal);
        Assert.Contains("2. How are reminders sent?", input, StringComparison.Ordinal);
        Assert.Contains("Security staff use the dashboard", input, StringComparison.Ordinal);
        Assert.Contains("summaryCheck: true", input, StringComparison.Ordinal);
        Assert.Contains("Q1: How many employees are there?", input, StringComparison.Ordinal);
        Assert.Contains("- chip: Other", input, StringComparison.Ordinal);
        Assert.Contains("Q2: Who uses the app?", input, StringComparison.Ordinal);
    }

    // Model không nhận response_format ⇒ Value null, phán quyết được bóc từ văn xuôi (kèm hàng rào ```json)
    // — cùng đường lùi với mọi lượt structured khác của repo.
    [Fact]
    public async Task JudgeTurn_ReadsTheVerdictFromPlainText_WhenStructuredOutputIsUnavailable()
    {
        var llm = new FakeLlm
        {
            Content = "```json\n{ \"repeats\": [1], \"emptyChips\": [ { \"question\": 1, \"chips\": [\"Khác\"] } ], "
                      + "\"userMentionsExistingSource\": true, \"asksForFile\": false }\n```"
        };

        var verdict = await new InterviewJudge(llm, new StubPrompts()).JudgeTurnAsync(
            Guid.NewGuid(), new[] { "câu cũ?" }, "trả lời", new[] { Q("câu cũ viết lại?", "A", "Khác") }, false, Ba, Model);

        Assert.Equal(new[] { 1 }, verdict.Repeats);
        Assert.Equal("Khác", Assert.Single(Assert.Single(verdict.EmptyChips).Chips));
        Assert.True(verdict.UserMentionsExistingSource);
        Assert.False(verdict.AsksForFile);
    }

    // FAIL-OPEN, ba kiểu hỏng: lời gọi báo lỗi, lời gọi ném ngoại lệ, model trả thứ không phải JSON. Cả ba
    // đều phải ra một phán quyết RỖNG chứ không ném ra khỏi lượt chat.
    [Theory]
    [InlineData("fail")]
    [InlineData("throw")]
    [InlineData("garbage")]
    public async Task ABrokenJudgeCall_YieldsAnEmptyVerdict_NeverAnException(string mode)
    {
        var llm = new FakeLlm { Fail = mode == "fail", Throw = mode == "throw", Content = "xin lỗi, tôi không chắc" };
        var judge = new InterviewJudge(llm, new StubPrompts());

        var turn = await judge.JudgeTurnAsync(
            Guid.NewGuid(), new[] { "câu cũ?" }, "trả lời", new[] { Q("câu mới?", "A", "B") }, false, Ba, Model);
        var coverage = await judge.JudgeOpenQuestionsAsync(
            Guid.NewGuid(), Array.Empty<CoverageMapItem>(),
            new[] { new OpenQuestionEntry { Group = "Quy mô sử dụng", Text = "bao nhiêu người dùng" } }, Ba, Model);

        Assert.Empty(turn.Repeats);
        Assert.Empty(turn.EmptyChips);
        Assert.False(turn.UserMentionsExistingSource);
        Assert.False(turn.AsksForFile);
        Assert.Empty(coverage.Drop);
    }

    // Người dùng huỷ lượt thì phải huỷ thật — đó KHÔNG phải một lỗi để nuốt.
    [Fact]
    public async Task Cancellation_IsNotSwallowed()
    {
        var llm = new FakeLlm { Cancel = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new InterviewJudge(llm, new StubPrompts()).JudgeTurnAsync(
                Guid.NewGuid(), new[] { "câu cũ?" }, "trả lời", new[] { Q("câu mới?") }, false, Ba, Model));
    }

    // Không có gì để phán thì không tốn một lời gọi nào.
    [Fact]
    public async Task NothingToJudge_MakesNoCall()
    {
        var llm = new FakeLlm();
        var judge = new InterviewJudge(llm, new StubPrompts());

        await judge.JudgeTurnAsync(Guid.NewGuid(), new[] { "câu cũ?" }, null, Array.Empty<InterviewJudge.TurnQuestion>(), false, Ba, Model);
        await judge.JudgeOpenQuestionsAsync(Guid.NewGuid(), Array.Empty<CoverageMapItem>(), Array.Empty<OpenQuestionEntry>(), Ba, Model);

        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task JudgeOpenQuestions_SendsEveryRowWithItsStatusAndKnownItems_AndTheNumberedQuestions()
    {
        var llm = new FakeLlm
        {
            Structured = new CoverageQuestionVerdict
            {
                Drop = { new CoverageQuestionDrop { Question = 1, Reason = CoverageQuestionDrop.Answered } }
            }
        };
        const string bullets = "- ★ Mục tiêu / bài toán: [RÕ] Quản lý JD.\n"
                               + "- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Mã JD duy nhất. còn thiếu: mã JD có được trùng không";

        var verdict = await new InterviewJudge(llm, new StubPrompts()).JudgeOpenQuestionsAsync(
            Guid.NewGuid(), CoverageMapFixture.Items(bullets), CoverageMapFixture.Questions(bullets), Ba, Model);

        Assert.Equal(1, Assert.Single(verdict.Drop).Question);
        Assert.Equal(InterviewJudge.CoveragePurpose, llm.LastPurpose);
        Assert.Equal(InterviewJudge.CoveragePromptPath, llm.LastSystemPrompt);

        var input = llm.LastUserMessage!;
        Assert.Contains("- Mục tiêu / bài toán: RÕ", input, StringComparison.Ordinal);
        Assert.Contains("· Quản lý JD.", input, StringComparison.Ordinal);
        Assert.Contains("- Quy tắc nghiệp vụ & ràng buộc: MỘT PHẦN", input, StringComparison.Ordinal);
        Assert.Contains("Q1 [nhóm: Quy tắc nghiệp vụ & ràng buộc]: mã JD có được trùng không", input, StringComparison.Ordinal);
    }

    // Sổ "đã hỏi" đi vào prompt ở MỌI lượt nên có trần — và phải giữ các câu MỚI NHẤT, vì hỏi lại gần như
    // luôn là hỏi lại một câu của vài lượt vừa qua.
    [Fact]
    public void TheLedgerIsCapped_KeepingTheMostRecentQuestions()
    {
        var asked = Enumerable.Range(1, InterviewJudge.MaxAskedQuestions + 5).Select(i => $"câu hỏi thứ {i}?").ToList();

        var input = InterviewJudge.RenderTurnInput(asked, "ok", new[] { Q("câu mới?") }, summaryCheck: false);

        Assert.DoesNotContain("câu hỏi thứ 5?", input, StringComparison.Ordinal);
        Assert.Contains("câu hỏi thứ 6?", input, StringComparison.Ordinal);
        Assert.Contains($"câu hỏi thứ {InterviewJudge.MaxAskedQuestions + 5}?", input, StringComparison.Ordinal);
    }

    // Hai prompt phải nói rõ rằng chúng phán theo NGHĨA ở mọi ngôn ngữ — đó là toàn bộ lý do lượt này tồn
    // tại thay cho các bảng cụm tiếng Việt — và phải khai đúng các trường mà code đọc.
    [Theory]
    [InlineData(InterviewJudge.TurnPromptPath, new[] { "repeats", "emptyChips", "userMentionsExistingSource", "asksForFile" })]
    [InlineData(InterviewJudge.CoveragePromptPath, new[] { "drop", "answered", "hollow", "not-a-question" })]
    public void ThePrompts_DeclareTheFieldsTheCodeReads_AndJudgeAnyLanguage(string path, string[] fields)
    {
        var prompt = PromptFixture.Read(path);

        Assert.Contains("bất kỳ ngôn ngữ nào", prompt, StringComparison.Ordinal);
        Assert.All(fields, field => Assert.Contains(field, prompt, StringComparison.Ordinal));
    }

    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public bool Fail;
        public bool Throw;
        public bool Cancel;
        public string Content = "{}";
        public object? Structured;
        public string? LastPurpose;
        public string? LastSystemPrompt;
        public string? LastUserMessage;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Trọng tài chỉ đi đường structured output.");

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            Calls++;
            LastPurpose = logContext.Purpose;
            LastSystemPrompt = messages.First(m => m.Role == ChatRole.System).Text;
            LastUserMessage = messages.Last(m => m.Role == ChatRole.User).Text;

            if (Cancel)
                throw new OperationCanceledException();
            if (Throw)
                throw new InvalidOperationException("budget exceeded");
            if (Fail)
                return Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "boom" }, (T?)null));

            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = Content }, Structured as T));
        }
    }

    // Trả về chính ĐƯỜNG DẪN prompt để test soi được lượt nào dùng prompt nào.
    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => relativePath;
    }
}
