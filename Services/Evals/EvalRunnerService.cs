using System.Diagnostics;
using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Evals;

/// <summary>
/// Thực thi MỘT EvalRun: với từng scenario đang bật — (1) gọi model MỤC TIÊU với system = nội dung
/// HIỆN HÀNH của template prompt + user = đầu vào scenario; (2) đưa output cho model JUDGE chấm 1–5
/// theo tiêu chí scenario (prompt Eval/judge.v1.md); (3) lưu EvalResult và cập nhật tiến độ để UI poll.
/// <para>
/// Lời gọi model tái dùng middleware <see cref="ModelCallLoggingChatClient"/> (deadline, trần token,
/// dựng result, map lỗi) nhưng với <see cref="NullModelCallLogger"/> — eval không thuộc project/agent
/// nào nên không ghi AgentModelCallLogs (token/lỗi đã nằm trên EvalResult) và không đi qua budget guard
/// theo-project. Lỗi TỪNG scenario không làm gãy run (ghi kết quả lỗi rồi chạy tiếp); chỉ lỗi mức run
/// (model bị xoá...) mới đánh Failed.
/// </para>
/// </summary>
public class EvalRunnerService
{
    // Nhiệt độ cố định để kết quả giữa các run so sánh được: target thấp (ít ngẫu nhiên), judge = 0.
    private const double TargetTemperature = 0.2;
    private const double JudgeTemperature = 0.0;

    // Persona ấm hơn BA một chút: người thật trả lời không đều tăm tắp, và một persona quá "ngoan" sẽ
    // che mất chính khuyết điểm mà bài kiểm tra cần lộ ra.
    private const double PersonaTemperature = 0.4;

    // Trần lượt cho phỏng vấn mô phỏng. Phỏng vấn thật hiếm khi quá con số này; chạm trần chính là một
    // KẾT QUẢ (phỏng vấn không tới đích) chứ không phải lỗi hạ tầng.
    private const int MaxInterviewTurns = 25;

    // Agent "đại diện" cho ModelCallLogContext (middleware chỉ dùng RoleKey.GetTitle() cho progress line;
    // logger là no-op nên danh tính agent không quan trọng với eval).
    private static readonly Agent EvalAgentStub = new();

    private readonly AppDbContext _db;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly PromptTemplateService _prompts;
    private readonly IPromptOverrideProvider _promptOverrides;
    private readonly BAChatReplyParser _replyParser;
    private readonly ILogger<EvalRunnerService> _logger;
    private readonly LlmSettings _llmSettings;

    public EvalRunnerService(
        AppDbContext db,
        IChatClientFactory chatClientFactory,
        PromptTemplateService prompts,
        IPromptOverrideProvider promptOverrides,
        BAChatReplyParser replyParser,
        LlmSettings llmSettings,
        ILogger<EvalRunnerService> logger)
    {
        _db = db;
        _chatClientFactory = chatClientFactory;
        _prompts = prompts;
        _promptOverrides = promptOverrides;
        _replyParser = replyParser;
        _logger = logger;
        _llmSettings = llmSettings;
    }

    public async Task RunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _db.EvalRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run == null)
            return;

        run.Status = EvalRunStatus.Running;
        run.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var targetModel = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == run.TargetModelId, cancellationToken);
        var judgeModel = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == run.JudgeModelId, cancellationToken);

        if (targetModel == null || judgeModel == null)
        {
            await FailRunAsync(run, "Model mục tiêu hoặc model judge không còn tồn tại.", cancellationToken);
            return;
        }

        var scenarios = await LoadScenariosAsync(run, cancellationToken);
        if (scenarios.Count == 0)
        {
            await FailRunAsync(run, "Không có scenario đang bật nào khớp bộ lọc của run.", cancellationToken);
            return;
        }

        // Chốt lại tổng theo bộ scenario THẬT lúc chạy (có thể đã thêm/tắt scenario từ lúc bấm nút).
        run.ScenarioCount = scenarios.Count;
        run.CompletedCount = 0;
        await _db.SaveChangesAsync(cancellationToken);

        var scores = new List<int>();

        foreach (var scenario in scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Người dùng bấm huỷ giữa chừng: dừng TRƯỚC khi mở scenario kế tiếp. Kết quả đã chạy xong giữ
            // nguyên (chúng đã trả tiền rồi, vứt đi không lợi ai) — run chỉ mang trạng thái Cancelled kèm
            // tiến độ dở dang để người xem biết điểm TB này tính trên bao nhiêu scenario.
            if (await IsCancelRequestedAsync(run.Id, cancellationToken))
            {
                await FinishRunAsync(run, EvalRunStatus.Cancelled,
                    $"Đã huỷ theo yêu cầu sau {run.CompletedCount}/{run.ScenarioCount} scenario.", cancellationToken);
                return;
            }

            var result = new EvalResult
            {
                EvalRunId = run.Id,
                EvalScenarioId = scenario.Id,
                ScenarioName = scenario.Name
            };

            try
            {
                await EvaluateScenarioAsync(scenario, targetModel, judgeModel, result, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // shutdown: run còn Running sẽ được worker recover thành Failed lúc khởi động lại.
            }
            catch (Exception ex)
            {
                // Một scenario nổ bất ngờ (vd template prompt bị xoá) không được làm gãy cả run.
                _logger.LogError(ex, "Eval scenario {ScenarioId} failed unexpectedly in run {RunId}.", scenario.Id, run.Id);
                result.IsSuccess = false;
                result.ErrorMessage = ex.Message;
            }

            if (result.Score is int score)
                scores.Add(score);

            _db.EvalResults.Add(result);
            run.CompletedCount++;
            run.TotalTokens += result.TargetTokens + result.JudgeTokens;
            run.TotalCost += result.TargetCost + result.JudgeCost;
            run.AverageScore = scores.Count == 0 ? null : Math.Round(scores.Average(), 2);
            await _db.SaveChangesAsync(cancellationToken);
        }

        await FinishRunAsync(run, EvalRunStatus.Completed, null, cancellationToken);
    }

    // Cờ huỷ do controller đặt trên MỘT DbContext khác — phải hỏi lại DB chứ không đọc entity đang track.
    private async Task<bool> IsCancelRequestedAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.EvalRuns
            .AsNoTracking()
            .Where(x => x.Id == runId)
            .Select(x => x.CancelRequestedAt)
            .FirstOrDefaultAsync(cancellationToken) != null;

    private Task EvaluateScenarioAsync(EvalScenario scenario, AiModel targetModel, AiModel judgeModel, EvalResult result, CancellationToken cancellationToken) =>
        scenario.Kind == EvalScenarioKind.Interview
            ? EvaluateInterviewScenarioAsync(scenario, targetModel, judgeModel, result, cancellationToken)
            : EvaluatePromptScenarioAsync(scenario, targetModel, judgeModel, result, cancellationToken);

    /// <summary>
    /// PHỎNG VẤN MÔ PHỎNG: chạy trọn một cuộc hỏi–đáp giữa BA (prompt đang đo) và một model đóng vai
    /// người dùng nghiệp vụ theo hồ sơ persona, tới khi BA mời bấm "Write Requirement" hoặc chạm trần lượt.
    ///
    /// <para>
    /// Đây là tầng eval mà bộ một-lượt không với tới. Chất lượng yêu cầu không được quyết bởi một câu trả
    /// lời đẹp, mà bởi cả cuộc phỏng vấn: nó có tới đích không, tốn bao nhiêu lượt của người dùng, và có
    /// tự phá các quy tắc chính prompt đặt ra không. Kết quả gồm hai phần: các con số ĐO ĐƯỢC
    /// (<see cref="InterviewTranscript.Measure"/>) và điểm judge chấm trên toàn transcript.
    /// </para>
    /// </summary>
    private async Task EvaluateInterviewScenarioAsync(EvalScenario scenario, AiModel targetModel, AiModel judgeModel, EvalResult result, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var promptOverride = _promptOverrides.GetActiveOverride(scenario.PromptKey);
        var systemPrompt = promptOverride?.Content ?? _prompts.Get(scenario.PromptKey);
        result.PromptVersionId = promptOverride?.Id;
        result.PromptVersionNumber = promptOverride?.VersionNumber;

        var personaPrompt = _prompts.Get("Eval/persona.v1.md").Replace("{{persona}}", scenario.UserInput);
        var turns = new List<InterviewTurn>();
        var conversation = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };

        // Lượt mở màn của "người dùng": một câu mô tả sơ sài như người thật vẫn mở đầu, để BA phải tự đào.
        var userMessage = "Chào bạn, mình muốn làm một ứng dụng cho công việc của mình.";

        for (var i = 0; i < MaxInterviewTurns; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            conversation.Add(new ChatMessage(ChatRole.User, userMessage));
            var baCall = await CallModelWithHistoryAsync(targetModel, conversation, TargetTemperature, cancellationToken);
            result.TargetTokens += baCall.TotalTokens;
            result.TargetCost += LlmCost.Usd(baCall.PromptTokens, baCall.CompletionTokens,
                targetModel.InputPricePerMillionTokens, targetModel.OutputPricePerMillionTokens);

            if (!baCall.IsSuccess)
            {
                Finish(false, $"Lời gọi model BA lỗi ở lượt {i + 1}: {baCall.ErrorMessage}");
                return;
            }

            // BA được yêu cầu trả JSON {message, suggestions,…}; parser dùng chung với luồng chat thật nên
            // model trả văn xuôi vẫn đo được (fallback về text thuần) thay vì làm hỏng cả lượt eval.
            var reply = _replyParser.Parse(baCall.Content);
            var baMessage = string.IsNullOrWhiteSpace(reply.Message) ? baCall.Content.Trim() : reply.Message.Trim();
            conversation.Add(new ChatMessage(ChatRole.Assistant, baCall.Content));

            var personaCall = await CallModelAsync(targetModel, personaPrompt, BuildPersonaInput(turns, baMessage, reply.Suggestions), PersonaTemperature, cancellationToken);
            result.TargetTokens += personaCall.TotalTokens;
            result.TargetCost += LlmCost.Usd(personaCall.PromptTokens, personaCall.CompletionTokens,
                targetModel.InputPricePerMillionTokens, targetModel.OutputPricePerMillionTokens);

            if (!personaCall.IsSuccess)
            {
                Finish(false, $"Lời gọi model persona lỗi ở lượt {i + 1}: {personaCall.ErrorMessage}");
                return;
            }

            userMessage = personaCall.Content.Trim();
            turns.Add(new InterviewTurn(baMessage, reply.Suggestions, userMessage));

            // BA đã mời bấm nút ⇒ phỏng vấn tới đích, dừng ngay (chạy tiếp chỉ tốn token).
            if (RequirementReadinessGate.IsWriteRequirementInvite(baMessage))
                break;
        }

        var metrics = InterviewTranscript.Measure(turns);
        var transcript = InterviewTranscript.Render(turns);
        result.Output = $"{metrics.Format()}\n\n---\n\n{transcript}";

        var judgeCall = await CallModelAsync(
            judgeModel, _prompts.Get("Eval/judge.v1.md"), BuildInterviewJudgeInput(scenario, metrics, transcript), JudgeTemperature, cancellationToken);

        result.JudgeTokens = judgeCall.TotalTokens;
        result.JudgeCost = LlmCost.Usd(judgeCall.PromptTokens, judgeCall.CompletionTokens,
            judgeModel.InputPricePerMillionTokens, judgeModel.OutputPricePerMillionTokens);

        if (!judgeCall.IsSuccess)
        {
            Finish(false, $"Lời gọi judge lỗi: {judgeCall.ErrorMessage}");
            return;
        }

        if (!EvalJudgeParser.TryParse(judgeCall.Content, out var verdict))
        {
            result.JudgeReasoning = judgeCall.Content;
            Finish(false, "Judge trả về không đúng định dạng JSON {score, reasoning}.");
            return;
        }

        ApplyVerdict(result, verdict);
        Finish(true, null);

        void Finish(bool success, string? error)
        {
            stopwatch.Stop();
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.IsSuccess = success;
            result.ErrorMessage = error;
            // Transcript dở dang vẫn được giữ lại: nó là bằng chứng để hiểu vì sao lượt eval hỏng.
            if (!success && string.IsNullOrEmpty(result.Output) && turns.Count > 0)
                result.Output = InterviewTranscript.Render(turns);
        }
    }

    private static string BuildPersonaInput(IReadOnlyList<InterviewTurn> turns, string baMessage, IReadOnlyList<string> suggestions)
    {
        var sb = new StringBuilder();
        if (turns.Count > 0)
        {
            sb.AppendLine("## Cuộc trao đổi tới lúc này");
            foreach (var turn in turns)
            {
                sb.AppendLine($"- BA: {turn.BaMessage}");
                sb.AppendLine($"- Bạn đã trả lời: {turn.UserReply}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("## BA vừa nói với bạn");
        sb.AppendLine(baMessage);
        if (suggestions.Count > 0)
            sb.AppendLine($"(các đáp án BA gợi ý sẵn: {string.Join(" · ", suggestions)})");
        sb.AppendLine();
        sb.Append("Trả lời câu này bằng đúng lời của bạn (ngắn gọn, bám hồ sơ vai diễn).");
        return sb.ToString();
    }

    private static string BuildInterviewJudgeInput(EvalScenario scenario, InterviewMetrics metrics, string transcript) =>
        $"""
         ## Hồ sơ người dùng được mô phỏng (điều BA CẦN khai thác cho ra)
         {scenario.UserInput}

         ## Số liệu đo được của cuộc phỏng vấn
         {metrics.Format()}

         ## Tiêu chí chấm
         {scenario.Criteria}

         ## Toàn bộ cuộc phỏng vấn cần chấm
         {transcript}
         """;

    private async Task EvaluatePromptScenarioAsync(EvalScenario scenario, AiModel targetModel, AiModel judgeModel, EvalResult result, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // (1) Model mục tiêu trả lời tình huống với NỘI DUNG HIỆN HÀNH của template prompt. Hỏi provider
        // TRƯỚC để ghi lại đã đo phiên bản NÀO: bản DB active (Prompt Studio) ⇒ dùng + snapshot id/số
        // phiên bản lên kết quả; không có ⇒ nội dung file (PromptVersionId null = "file").
        var promptOverride = _promptOverrides.GetActiveOverride(scenario.PromptKey);
        var systemPrompt = promptOverride?.Content ?? _prompts.Get(scenario.PromptKey);
        result.PromptVersionId = promptOverride?.Id;
        result.PromptVersionNumber = promptOverride?.VersionNumber;
        var targetResult = await CallModelAsync(targetModel, systemPrompt, scenario.UserInput, TargetTemperature, cancellationToken);

        result.Output = targetResult.Content;
        result.TargetTokens = targetResult.TotalTokens;
        result.TargetCost = LlmCost.Usd(
            targetResult.PromptTokens, targetResult.CompletionTokens,
            targetModel.InputPricePerMillionTokens, targetModel.OutputPricePerMillionTokens);

        if (!targetResult.IsSuccess)
        {
            stopwatch.Stop();
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.IsSuccess = false;
            result.ErrorMessage = $"Lời gọi model mục tiêu lỗi: {targetResult.ErrorMessage}";
            return;
        }

        // (2) Judge chấm output theo tiêu chí của scenario.
        var judgeResult = await CallModelAsync(
            judgeModel, _prompts.Get("Eval/judge.v1.md"), BuildJudgeInput(scenario, targetResult.Content), JudgeTemperature, cancellationToken);

        stopwatch.Stop();
        result.DurationMs = stopwatch.ElapsedMilliseconds;
        result.JudgeTokens = judgeResult.TotalTokens;
        result.JudgeCost = LlmCost.Usd(
            judgeResult.PromptTokens, judgeResult.CompletionTokens,
            judgeModel.InputPricePerMillionTokens, judgeModel.OutputPricePerMillionTokens);

        if (!judgeResult.IsSuccess)
        {
            result.IsSuccess = false;
            result.ErrorMessage = $"Lời gọi judge lỗi: {judgeResult.ErrorMessage}";
            return;
        }

        if (!EvalJudgeParser.TryParse(judgeResult.Content, out var verdict))
        {
            result.IsSuccess = false;
            result.ErrorMessage = "Judge trả về không đúng định dạng JSON {score, reasoning}.";
            result.JudgeReasoning = judgeResult.Content;
            return;
        }

        ApplyVerdict(result, verdict);
        result.IsSuccess = true;
    }

    // Bảng đối chiếu từng tiêu chí là phần MỞ RỘNG của judge: model không trả thì CriteriaJson = null và
    // kết quả vẫn đủ dùng (điểm + lý do) — không coi đây là lỗi chấm.
    private static void ApplyVerdict(EvalResult result, EvalJudgeVerdict verdict)
    {
        result.Score = verdict.Score;
        result.JudgeReasoning = verdict.Reasoning;
        result.CriteriaJson = EvalJudgeParser.ToJson(verdict.Criteria);
    }

    private static string BuildJudgeInput(EvalScenario scenario, string output) =>
        $"""
         ## Đầu vào của tình huống
         {scenario.UserInput}

         ## Tiêu chí chấm
         {scenario.Criteria}

         ## Câu trả lời của AI cần chấm
         {output}
         """;

    private Task<LlmCallResult> CallModelAsync(AiModel model, string systemPrompt, string userPrompt, double temperature, CancellationToken cancellationToken) =>
        CallModelWithHistoryAsync(model, new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        }, temperature, cancellationToken);

    // Một lời gọi model cho eval, gửi TRỌN lịch sử hội thoại — phỏng vấn mô phỏng cần BA thấy các lượt
    // trước (không thì mỗi lượt nó lại hỏi từ đầu và bài kiểm tra đo nhầm thứ khác).
    //
    // Cùng đường ống dùng chung như LlmClient, chỉ khác hai chỗ: logger no-op (xem NullModelCallLogger) và
    // KHÔNG budget guard (eval không thuộc project nào). Deadline + trần token + map lỗi vẫn do middleware
    // lo. Không truyền onToken: eval chỉ cần kết quả đã gom đủ.
    private Task<LlmCallResult> CallModelWithHistoryAsync(AiModel model, List<ChatMessage> messages, double temperature, CancellationToken cancellationToken) =>
        new ModelCallPipeline(
                _chatClientFactory, model, new NullModelCallLogger(),
                new ModelCallLogContext(Guid.Empty, EvalAgentStub, "Eval"),
                new ModelCallOptions(_llmSettings.RequestTimeoutSeconds, ThrowOnFailure: false))
            .StreamAsync(messages, new ChatOptions { Temperature = (float)temperature }, onToken: null, cancellationToken);

    private async Task<List<EvalScenario>> LoadScenariosAsync(EvalRun run, CancellationToken cancellationToken)
    {
        var query = _db.EvalScenarios.AsNoTracking().Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(run.PromptKey))
            query = query.Where(x => x.PromptKey == run.PromptKey);

        return await query
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    private Task FailRunAsync(EvalRun run, string error, CancellationToken cancellationToken) =>
        FinishRunAsync(run, EvalRunStatus.Failed, error, cancellationToken);

    private async Task FinishRunAsync(EvalRun run, EvalRunStatus status, string? error, CancellationToken cancellationToken)
    {
        run.Status = status;
        run.Error = error;
        run.FinishedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
