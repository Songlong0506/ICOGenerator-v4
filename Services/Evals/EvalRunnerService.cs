using System.Diagnostics;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
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
    private const int DefaultRequestTimeoutSeconds = 600;

    // Agent "đại diện" cho ModelCallLogContext (middleware chỉ dùng RoleKey.GetTitle() cho progress line;
    // logger là no-op nên danh tính agent không quan trọng với eval).
    private static readonly Agent EvalAgentStub = new();

    private readonly AppDbContext _db;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly PromptTemplateService _prompts;
    private readonly IPromptOverrideProvider _promptOverrides;
    private readonly ILogger<EvalRunnerService> _logger;
    private readonly int _requestTimeoutSeconds;

    public EvalRunnerService(
        AppDbContext db,
        IChatClientFactory chatClientFactory,
        PromptTemplateService prompts,
        IPromptOverrideProvider promptOverrides,
        IConfiguration configuration,
        ILogger<EvalRunnerService> logger)
    {
        _db = db;
        _chatClientFactory = chatClientFactory;
        _prompts = prompts;
        _promptOverrides = promptOverrides;
        _logger = logger;
        _requestTimeoutSeconds = configuration.GetValue("Llm:RequestTimeoutSeconds", DefaultRequestTimeoutSeconds);
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

        run.Status = EvalRunStatus.Completed;
        run.FinishedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task EvaluateScenarioAsync(EvalScenario scenario, AiModel targetModel, AiModel judgeModel, EvalResult result, CancellationToken cancellationToken)
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

        if (!EvalJudgeParser.TryParse(judgeResult.Content, out var score, out var reasoning))
        {
            result.IsSuccess = false;
            result.ErrorMessage = "Judge trả về không đúng định dạng JSON {score, reasoning}.";
            result.JudgeReasoning = judgeResult.Content;
            return;
        }

        result.Score = score;
        result.JudgeReasoning = reasoning;
        result.IsSuccess = true;
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

    // Cùng cách dựng client như LlmClient.BuildClient nhưng logger no-op (xem NullModelCallLogger) và
    // không budget guard (eval không thuộc project nào). Deadline + trần token + map lỗi vẫn do middleware lo.
    private async Task<LlmCallResult> CallModelAsync(AiModel model, string systemPrompt, string userPrompt, double temperature, CancellationToken cancellationToken)
    {
        LlmCallResult? captured = null;
        var client = _chatClientFactory.Create(model)
            .AsBuilder()
            .Use(inner => new ModelCallLoggingChatClient(
                inner, model, new NullModelCallLogger(), new ModelCallLogContext(Guid.Empty, EvalAgentStub, "Eval"),
                _requestTimeoutSeconds, throwOnFailure: false, onCompleted: r => captured = r))
            .Build();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        await foreach (var _ in client.GetStreamingResponseAsync(messages, new ChatOptions { Temperature = (float)temperature }, cancellationToken).ConfigureAwait(false))
        {
            // Buffered: middleware dựng result đầy đủ và trả qua onCompleted; không cần stream token cho eval.
        }

        return captured ?? throw new InvalidOperationException("Model call produced no result.");
    }

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

    private async Task FailRunAsync(EvalRun run, string error, CancellationToken cancellationToken)
    {
        run.Status = EvalRunStatus.Failed;
        run.Error = error;
        run.FinishedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
