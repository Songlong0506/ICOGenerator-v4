using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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

    // Lượt BA là HAI lời gọi: repeats nhận sổ "đã hỏi" + chữ câu hỏi; bốn phán quyết dễ nhận câu hỏi kèm chip
    // nhưng KHÔNG nhận sổ — phần nặng nhất của đầu vào, và là lý do lời gọi dễ chạy được trên model rẻ.
    [Fact]
    public async Task JudgeTurn_SendsTheLedgerOnlyToTheRepeatCall_AndTheChipsOnlyToTheSignalCall()
    {
        var llm = new FakeLlm
        {
            Structured =
            {
                new TurnRepeatVerdict { Repeats = { 2 } },
                new TurnSignalVerdict { EmptyChips = { new TurnJudgeChips { Question = 1, Chips = { "Other" } } }, AsksForFile = true }
            }
        };

        var verdict = await new InterviewJudge(llm, new StubPrompts()).JudgeTurnAsync(
            Guid.NewGuid(),
            new[] { "Who will use the app?", "How are reminders sent?" },
            "Security staff use the dashboard",
            new[] { Q("How many employees are there?", "Under 500", "Other"), Q("Who uses the app?") },
            summaryCheck: true, Ba, Model);

        Assert.Equal(new[] { 2 }, verdict.Repeats);
        Assert.Equal("Other", Assert.Single(Assert.Single(verdict.EmptyChips).Chips));
        Assert.True(verdict.AsksForFile);

        var repeat = llm.Call(InterviewJudge.TurnPurpose);
        Assert.Equal(InterviewJudge.TurnPromptPath, repeat.System);
        Assert.Contains("1. Who will use the app?", repeat.User, StringComparison.Ordinal);
        Assert.Contains("2. How are reminders sent?", repeat.User, StringComparison.Ordinal);
        Assert.Contains("Security staff use the dashboard", repeat.User, StringComparison.Ordinal);
        Assert.Contains("summaryCheck: true", repeat.User, StringComparison.Ordinal);
        Assert.Contains("Q1: How many employees are there?", repeat.User, StringComparison.Ordinal);
        Assert.Contains("Q2: Who uses the app?", repeat.User, StringComparison.Ordinal);
        Assert.DoesNotContain("chip:", repeat.User, StringComparison.Ordinal);

        var signal = llm.Call(InterviewJudge.SignalPurpose);
        Assert.Equal(InterviewJudge.SignalPromptPath, signal.System);
        Assert.Contains("Security staff use the dashboard", signal.User, StringComparison.Ordinal);
        Assert.Contains("Q1: How many employees are there?", signal.User, StringComparison.Ordinal);
        Assert.Contains("- chip: Other", signal.User, StringComparison.Ordinal);
        Assert.DoesNotContain("Who will use the app?", signal.User, StringComparison.Ordinal);
    }

    // Sổ "đã hỏi" rỗng ⇒ chưa có gì để hỏi lại, và caller bỏ qua repeats khi đó — lời gọi đắt hơn không được
    // tốn. Lời gọi dễ vẫn chạy: chip rỗng ở câu hỏi đầu tiên của buổi vẫn là chip rỗng.
    [Fact]
    public async Task AnEmptyLedger_SkipsTheRepeatCall_ButStillJudgesTheSignals()
    {
        var llm = new FakeLlm();

        await new InterviewJudge(llm, new StubPrompts()).JudgeTurnAsync(
            Guid.NewGuid(), new[] { "  " }, "xin chào", new[] { Q("Ai dùng app?", "Nhân viên", "Khác") }, false, Ba, Model);

        Assert.Equal(new[] { InterviewJudge.SignalPurpose }, llm.Calls.Select(c => c.Purpose));
    }

    // Lượt không hỏi câu nào (vd. chỉ còn cửa cho lượt xin file) ⇒ không có gì để hỏi lại.
    [Fact]
    public async Task ATurnWithoutQuestions_SkipsTheRepeatCall()
    {
        var llm = new FakeLlm();

        await new InterviewJudge(llm, new StubPrompts()).JudgeTurnAsync(
            Guid.NewGuid(), new[] { "câu cũ?" }, "bên em dùng 2 file excel", Array.Empty<InterviewJudge.TurnQuestion>(), false, Ba, Model);

        Assert.Equal(new[] { InterviewJudge.SignalPurpose }, llm.Calls.Select(c => c.Purpose));
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

        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task JudgeOpenQuestions_SendsEveryRowWithItsStatusAndKnownItems_AndTheNumberedQuestions()
    {
        var llm = new FakeLlm
        {
            Structured =
            {
                new CoverageQuestionVerdict
                {
                    Drop = { new CoverageQuestionDrop { Question = 1, Reason = CoverageQuestionDrop.Answered } }
                }
            }
        };
        const string bullets = "- ★ Mục tiêu / bài toán: [RÕ] Quản lý JD.\n"
                               + "- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Mã JD duy nhất. còn thiếu: mã JD có được trùng không";

        var verdict = await new InterviewJudge(llm, new StubPrompts()).JudgeOpenQuestionsAsync(
            Guid.NewGuid(), CoverageMapFixture.Items(bullets), CoverageMapFixture.Questions(bullets), Ba, Model);

        Assert.Equal(1, Assert.Single(verdict.Drop).Question);
        var call = llm.Call(InterviewJudge.CoveragePurpose);
        Assert.Equal(InterviewJudge.CoveragePromptPath, call.System);

        var input = call.User;
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

        var input = InterviewJudge.RenderRepeatInput(asked, "ok", new[] { Q("câu mới?") }, summaryCheck: false);

        Assert.DoesNotContain("câu hỏi thứ 5?", input, StringComparison.Ordinal);
        Assert.Contains("câu hỏi thứ 6?", input, StringComparison.Ordinal);
        Assert.Contains($"câu hỏi thứ {InterviewJudge.MaxAskedQuestions + 5}?", input, StringComparison.Ordinal);
    }

    // MODEL TRỌNG TÀI RIÊNG (Agent.JudgeAiModel): khai thì bốn phán quyết dễ đi model đó, không khai — hoặc
    // khai một model đã tắt — thì đi model BA. Phán quyết repeats thì LUÔN đi model BA: nó là phán quyết khó
    // nhất, và là chiều duy nhất mà phán sai thì chặn oan một câu hỏi mới.
    [Theory]
    [InlineData(true, "haiku")]
    [InlineData(false, "sonnet")]
    public async Task TheSignalsRunOnTheJudgeModel_TheRepeatsAlwaysOnTheBaModel(bool judgeActive, string expectedSignalModel)
    {
        var (ba, sonnet) = BaWithJudge(judgeActive);
        var llm = new FakeLlm { Structured = { new TurnRepeatVerdict { Repeats = { 1 } } } };

        var verdict = await new InterviewJudge(llm, new StubPrompts()).JudgeTurnAsync(
            Guid.NewGuid(), new[] { "câu cũ?" }, "trả lời", new[] { Q("câu cũ?") }, false, ba, sonnet);

        Assert.Equal("sonnet", llm.Call(InterviewJudge.TurnPurpose).Model);
        Assert.Equal(expectedSignalModel, llm.Call(InterviewJudge.SignalPurpose).Model);
        Assert.Equal(new[] { 1 }, verdict.Repeats);
    }

    // Trọng tài câu hỏi tồn đọng LUÔN đi model BA, kể cả khi agent khai model trọng tài: phán sai ở đây là
    // xoá oan một câu hỏi mới. Ca thật: model trọng tài rẻ xoá cả hai câu distiller vừa viết về danh mục
    // Tòa nhà/Khu vực, trái với chính ví dụ "ai được XOÁ" trong prompt.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheCoverageJudge_AlwaysRunsOnTheBaModel(bool judgeActive)
    {
        var (ba, sonnet) = BaWithJudge(judgeActive);
        var llm = new FakeLlm { Structured = { new CoverageQuestionVerdict { Drop = { new CoverageQuestionDrop { Question = 1, Reason = "hollow" } } } } };

        var verdict = await new InterviewJudge(llm, new StubPrompts()).JudgeOpenQuestionsAsync(
            Guid.NewGuid(), Array.Empty<CoverageMapItem>(),
            new[] { new OpenQuestionEntry { Group = "Quy mô sử dụng", Text = "các quy tắc khác (nếu có)" } }, ba, sonnet);

        Assert.Equal(new[] { "sonnet" }, llm.Calls.Select(c => c.Model));
        Assert.Single(verdict.Drop);
    }

    [Fact]
    public async Task ASignalCallInfrastructureFailure_FallsBackToTheBaModel_WithoutTouchingTheRepeatHalf()
    {
        var (ba, sonnet) = BaWithJudge(judgeActive: true);
        var llm = new FakeLlm
        {
            FailModelId = "haiku",
            Structured = { new TurnRepeatVerdict { Repeats = { 1 } }, new TurnSignalVerdict { AsksForFile = true } }
        };

        var verdict = await new InterviewJudge(llm, new StubPrompts()).JudgeTurnAsync(
            Guid.NewGuid(), new[] { "câu cũ?" }, "trả lời", new[] { Q("câu cũ?") }, false, ba, sonnet);

        Assert.Equal(
            new[] { "sonnet" },
            llm.Calls.Where(c => c.Purpose == InterviewJudge.TurnPurpose).Select(c => c.Model));
        Assert.Equal(
            new[] { "haiku", "sonnet" },
            llm.Calls.Where(c => c.Purpose == InterviewJudge.SignalPurpose).Select(c => c.Model));
        Assert.Equal(new[] { 1 }, verdict.Repeats);
        Assert.True(verdict.AsksForFile);
    }

    // Trả lời KHÔNG đọc được là chất lượng của model trọng tài — thứ kiểm toán trọng tài đo — chứ không phải
    // sự cố: không gọi lại, nửa đó rỗng (fail-open); nửa kia vẫn giữ phán quyết của nó.
    [Fact]
    public async Task AnUnreadableSignalAnswer_IsNotRetriedOnTheBaModel()
    {
        var (ba, sonnet) = BaWithJudge(judgeActive: true);
        var llm = new FakeLlm { GarbageModelId = "haiku", Structured = { new TurnRepeatVerdict { Repeats = { 1 } } } };

        var verdict = await new InterviewJudge(llm, new StubPrompts()).JudgeTurnAsync(
            Guid.NewGuid(), new[] { "câu cũ?" }, "trả lời", new[] { Q("câu mới?", "A", "Khác") }, false, ba, sonnet);

        Assert.Equal(new[] { "haiku" }, llm.Calls.Where(c => c.Purpose == InterviewJudge.SignalPurpose).Select(c => c.Model));
        Assert.Equal(new[] { 1 }, verdict.Repeats);
        Assert.Empty(verdict.EmptyChips);
    }

    // Có scope factory (production) thì lời gọi dễ chạy trên ILlmClient của một DI scope RIÊNG — song song
    // với lời gọi repeats mà không để hai lời gọi giẫm lên cùng một DbContext lúc ghi AI Call Logs.
    [Fact]
    public async Task WithAScopeFactory_TheSignalCallRunsOnItsOwnScopesClient()
    {
        var main = new FakeLlm { Structured = { new TurnRepeatVerdict { Repeats = { 1 } } } };
        var scoped = new FakeLlm { Structured = { new TurnSignalVerdict { AsksForFile = true } } };
        await using var provider = new ServiceCollection()
            .AddScoped<ILlmClient>(_ => scoped)
            .BuildServiceProvider();

        var verdict = await new InterviewJudge(main, new StubPrompts(), provider.GetRequiredService<IServiceScopeFactory>())
            .JudgeTurnAsync(Guid.NewGuid(), new[] { "câu cũ?" }, "trả lời", new[] { Q("câu cũ?") }, false, Ba, Model);

        Assert.Equal(new[] { InterviewJudge.TurnPurpose }, main.Calls.Select(c => c.Purpose));
        Assert.Equal(new[] { InterviewJudge.SignalPurpose }, scoped.Calls.Select(c => c.Purpose));
        Assert.Equal(new[] { 1 }, verdict.Repeats);
        Assert.True(verdict.AsksForFile);
    }

    // Dựng scope hỏng cũng là một kiểu trọng tài hỏng: nửa đó rỗng, không ném ra khỏi lượt chat.
    [Fact]
    public async Task AScopeThatCannotResolveTheClient_YieldsAnEmptySignalHalf()
    {
        var main = new FakeLlm { Structured = { new TurnRepeatVerdict { Repeats = { 1 } } } };
        await using var provider = new ServiceCollection().BuildServiceProvider();

        var verdict = await new InterviewJudge(main, new StubPrompts(), provider.GetRequiredService<IServiceScopeFactory>())
            .JudgeTurnAsync(Guid.NewGuid(), new[] { "câu cũ?" }, "trả lời", new[] { Q("câu cũ?") }, false, Ba, Model);

        Assert.Equal(new[] { 1 }, verdict.Repeats);
        Assert.False(verdict.AsksForFile);
    }

    private static (Agent Ba, AiModel Sonnet) BaWithJudge(bool judgeActive)
    {
        var sonnet = new AiModel { Id = Guid.NewGuid(), ModelId = "sonnet" };
        var haiku = new AiModel { Id = Guid.NewGuid(), ModelId = "haiku", IsActive = judgeActive };
        var ba = new Agent { Id = Guid.NewGuid(), AiModel = sonnet, AiModelId = sonnet.Id, JudgeAiModel = haiku, JudgeAiModelId = haiku.Id };
        return (ba, sonnet);
    }

    // Ba prompt phải nói rõ rằng chúng phán theo NGHĨA ở mọi ngôn ngữ — đó là toàn bộ lý do lượt này tồn
    // tại thay cho các bảng cụm tiếng Việt — và phải khai đúng các trường mà code đọc.
    [Theory]
    [InlineData(InterviewJudge.TurnPromptPath, new[] { "repeats", "summaryCheck" })]
    [InlineData(InterviewJudge.SignalPromptPath, new[] { "emptyChips", "yesNoChips", "userMentionsExistingSource", "asksForFile" })]
    [InlineData(InterviewJudge.CoveragePromptPath, new[] { "drop", "answered", "hollow", "not-a-question", "evidence" })]
    public void ThePrompts_DeclareTheFieldsTheCodeReads_AndJudgeAnyLanguage(string path, string[] fields)
    {
        var prompt = PromptFixture.Read(path);

        Assert.Contains("bất kỳ ngôn ngữ nào", prompt, StringComparison.Ordinal);
        Assert.All(fields, field => Assert.Contains(field, prompt, StringComparison.Ordinal));
    }

    private sealed class FakeLlm : ILlmClient
    {
        public sealed record Sent(string Purpose, string Model, string System, string User);

        public readonly List<Sent> Calls = new();
        public bool Fail;
        public bool Throw;
        public bool Cancel;
        public string Content = "{}";
        /// <summary>Phán quyết dựng sẵn, trả cho lời gọi nào hỏi đúng kiểu của nó.</summary>
        public readonly List<object> Structured = new();
        public string? FailModelId;
        public string? GarbageModelId;

        public Sent Call(string purpose) => Assert.Single(Calls, c => c.Purpose == purpose);

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Trọng tài chỉ đi đường structured output.");

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            lock (Calls)
                Calls.Add(new Sent(
                    logContext.Purpose, model.ModelId,
                    messages.First(m => m.Role == ChatRole.System).Text,
                    messages.Last(m => m.Role == ChatRole.User).Text));

            if (Cancel)
                throw new OperationCanceledException();
            if (Throw)
                throw new InvalidOperationException("budget exceeded");
            if (model.ModelId == GarbageModelId)
                return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "không phải JSON" }, (T?)null));
            if (Fail || model.ModelId == FailModelId)
                return Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "boom" }, (T?)null));

            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = Content }, Structured.OfType<T>().FirstOrDefault()));
        }
    }

    // Trả về chính ĐƯỜNG DẪN prompt để test soi được lượt nào dùng prompt nào.
    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => relativePath;
    }
}
