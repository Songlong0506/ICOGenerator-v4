using System.ClientModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// The single piece of per-model-call middleware shared by ALL execution paths — the agent's native
/// function-calling loop (<see cref="ICOGenerator.Services.Agents.AgentRunService"/>), the plain
/// streaming/structured chat used by <see cref="LlmClient"/> (BA, readiness, the prompt-based fallback),
/// and the eval harness (<see cref="ICOGenerator.Services.Evals.EvalRunnerService"/>).
/// As a <see cref="DelegatingChatClient"/> it sees exactly one model round-trip per call and owns every
/// cross-cutting concern that used to be duplicated between the hand-written <c>LlmClient</c> and the
/// agent-only <c>AgentModelCallChatClient</c>:
/// <list type="bullet">
///   <item>the budget circuit breaker (<see cref="ICOGenerator.Services.Budget.IBudgetGuard"/>) consulted
///         BEFORE each round-trip, so an over-budget run/chat is stopped before it spends more;</item>
///   <item>the single per-call deadline (the SDK's own network timeout is disabled in the factory);</item>
///   <item>the completion-token cap, recomputed per call from the current prompt size;</item>
///   <item>building the <see cref="LlmCallResult"/> + mapping API/timeout/other failures onto it;</item>
///   <item>request-shape (<see cref="ModelCallRequestPreview"/>) + DB logging via
///         <see cref="IModelCallLogger"/> (call-log UI unchanged);</item>
///   <item>(optional) the per-step "thinking" progress line, and surfacing a failed call as a thrown,
///         run-ending error (<see cref="ModelCallOptions.ThrowOnFailure"/>).</item>
/// </list>
/// The streaming override is the pass-through used by the agent loop and by <c>LlmClient</c>'s text path;
/// the non-streaming override is a true single round-trip, used by structured output. The built result is
/// handed back through <see cref="ModelCallOptions.OnCompleted"/> so a terminal consumer can return it
/// without rebuilding; the agent path ignores it and reads the streamed text instead.
///
/// Callers rarely construct this directly — <see cref="ModelCallPipeline"/> composes it over the per-model
/// client and captures the result for them.
/// </summary>
public sealed class ModelCallLoggingChatClient : DelegatingChatClient
{
    private readonly AiModel _model;
    private readonly IModelCallLogger _logger;
    private readonly ModelCallLogContext _context;
    private readonly ModelCallOptions _options;
    private readonly ModelCallImageStore? _imageStore;

    private int _step;

    /// <param name="imageStore">
    /// Có mặt ⇒ ảnh đi kèm request được gom lại để lưu ra đĩa cho màn call-log xem lại. Null ⇒ không gom
    /// (đường agent và eval: các lượt đó không bao giờ mang ảnh, gom chỉ tốn một bản copy bytes vô ích).
    /// </param>
    public ModelCallLoggingChatClient(
        IChatClient inner, AiModel model, IModelCallLogger logger,
        ModelCallLogContext context, ModelCallOptions options,
        ModelCallImageStore? imageStore = null) : base(inner)
    {
        _model = model;
        _logger = logger;
        _context = context;
        _options = options;
        _imageStore = imageStore;
    }

    /// <summary>
    /// Number of model calls made so far this run (auto-incremented per call). The agent orchestrator
    /// compares it against a phase's budget to tell a converged run (finished under budget) from one that
    /// exhausted its step budget.
    /// </summary>
    public int StepCount => _step;

    // True single round-trip — used by structured output (GetResponseAsync<T>) and any non-streaming caller.
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Budget circuit breaker: refuse BEFORE the round-trip (and before any logging) if the configured USD
        // cap is already reached, so an over-budget run/chat stops burning money. Throws BudgetExceededException,
        // intentionally outside the failure mapping below so it isn't relabelled "LLM call failed".
        await EnsureWithinBudgetAsync(cancellationToken).ConfigureAwait(false);

        var call = Begin(messages, options, streaming: false);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var response = await base.GetResponseAsync(call.Messages, call.Options, linkedCts.Token).ConfigureAwait(false);
            FinalizeSuccess(call.Result, call.Stopwatch, response, call.MaxTokens);
            await CompleteAsync(call.Result, call.Step).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller (e.g. app shutdown) cancelled — propagate so the worker treats it as a clean stop.
            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(call.Result, call.Stopwatch, call.Step, ex).ConfigureAwait(false);
            ThrowIfConfigured(call.Result, ex);
            // Swallowed: hand back an empty response so a structured caller falls back to manual parsing
            // (the failure is recorded on the LlmCallResult delivered via OnCompleted).
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Budget circuit breaker (see GetResponseAsync). In an iterator the await runs on the first MoveNextAsync,
        // so the consumer's await-foreach observes BudgetExceededException before any chunk or log is produced.
        await EnsureWithinBudgetAsync(cancellationToken).ConfigureAwait(false);

        var call = Begin(messages, options, streaming: true);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var updates = new List<ChatResponseUpdate>();
        await using var enumerator = base.GetStreamingResponseAsync(call.Messages, call.Options, linkedCts.Token)
            .GetAsyncEnumerator(linkedCts.Token);

        // Enumerate manually so a streaming error is caught and logged here (yield must stay out of try).
        var failed = false;
        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    break;
                update = enumerator.Current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await FailAsync(call.Result, call.Stopwatch, call.Step, ex).ConfigureAwait(false);
                ThrowIfConfigured(call.Result, ex);
                failed = true;
                break;
            }

            updates.Add(update);
            yield return update;
        }

        if (failed)
            yield break;

        var response = updates.ToChatResponse();
        FinalizeSuccess(call.Result, call.Stopwatch, response, call.MaxTokens);
        await CompleteAsync(call.Result, call.Step).ConfigureAwait(false);
    }

    // Per-call setup shared by both overrides: bump the step, size the request, build the log shape, and
    // emit the "thinking" progress line. Clones the options so the agent's shared instance is never mutated.
    private CallState Begin(IEnumerable<ChatMessage> messages, ChatOptions? options, bool streaming)
    {
        var step = ++_step;
        // Khóa định tuyến prompt cache của lời gọi này. Đặt Ở ĐÂY vì đây là chỗ duy nhất vừa cầm
        // ModelCallLogContext (biết project) vừa nằm TRÊN lời gọi HTTP trong cùng execution context, nên
        // giá trị chảy xuống đúng request của chính nó. Xem LlmCacheScope.
        LlmCacheScope.CacheKey = LlmCacheScope.KeyForProject(_context.ProjectId);
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var result = new LlmCallResult
        {
            ModelId = _model.ModelId,
            // ChatMessage.Text is empty for tool-result turns, so this slightly undercounts on tool-heavy
            // conversations — acceptable for an estimate used only for the cost/usage display.
            PromptTokens = TokenEstimator.Estimate(string.Join("\n", messageList.Select(m => m.Text)))
        };

        var maxTokens = MaxOutputTokenResolver.Resolve(_model, result.PromptTokens);
        var callOptions = options?.Clone() ?? new ChatOptions();
        callOptions.MaxOutputTokens = maxTokens;
        result.RequestJson = ModelCallRequestPreview.Build(_model, messageList, callOptions, maxTokens, streaming);
        result.ApproxRequestBytes = ModelCallRequestPreview.ApproxBodyBytes(messageList);
        // Ảnh gom TẠI ĐÂY chứ không ở chỗ ghi log: đây là nơi duy nhất còn cầm messages, và phải gom TRƯỚC
        // lời gọi vì lượt thất bại — đúng lượt người ta mở log ra soi — cũng cần xem lại ảnh đã gửi.
        if (_imageStore is { Enabled: true })
            result.RequestImages = ModelCallImageCollector.Collect(messageList, _imageStore.MaxBytesPerCall);

        _options.OnProgress?.Invoke("thinking", $"Agent {_context.Agent.RoleKey.GetTitle()} đang suy nghĩ… (bước {BudgetLabel(step)})", null);
        return new CallState(step, messageList, callOptions, result, Stopwatch.StartNew(), maxTokens);
    }

    private void FinalizeSuccess(LlmCallResult result, Stopwatch stopwatch, ChatResponse response, int maxTokens)
    {
        stopwatch.Stop();
        var text = response.Text ?? string.Empty;
        result.Content = text;
        result.ResponseText = text;
        result.DurationMs = stopwatch.ElapsedMilliseconds;
        result.HttpStatusCode = 200;
        result.IsSuccess = true;
        // finish_reason == "length" means the model hit its token cap mid-output (often truncated JSON);
        // flag it so a cut-off answer is distinguishable from a clean one.
        if (response.FinishReason == ChatFinishReason.Length)
            result.ErrorMessage = TokenLimitMessage(text, maxTokens);
        ApplyTokenCounts(result, response.Usage, text);
    }

    /// <summary>
    /// Chạm trần token mà KHÔNG trả ra chữ nào là một sự cố khác hẳn "câu trả lời bị cắt giữa chừng", và
    /// gần như luôn có đúng một thủ phạm: model REASONING tiêu sạch ngân sách output vào phần suy luận ẩn
    /// (reasoning token cũng tính vào trần này nhưng không hiện ra chữ nào). Người dùng đọc "phản hồi có
    /// thể bị cắt" rồi bấm "Thử lại" mãi cũng không thoát, vì lượt nào cũng cụt y như vậy — nên chỗ này
    /// phải nói thẳng ra hai nút xoay được: nâng trần, hoặc đổi model.
    /// </summary>
    private static string TokenLimitMessage(string text, int maxTokens) =>
        string.IsNullOrWhiteSpace(text)
            ? $"Model dùng hết hạn mức {maxTokens} token output mà KHÔNG trả ra chữ nào (finish_reason=length). "
              + "Model dạng reasoning tiêu ngân sách này vào phần suy luận ẩn, nên lượt cần câu trả lời dài "
              + "thì hết token trước khi kịp viết. Nâng Context Window của model ở trang Models (trần output "
              + "suy ra từ đó), hoặc chọn model khác cho agent này."
            : "Phản hồi có thể bị cắt do đạt giới hạn token (finish_reason=length).";

    // Prefer the provider's REAL token usage (UsageDetails on the response) over the ~4-chars/token estimate
    // so cost and the budget guard reflect what's actually billed. Each field falls back INDEPENDENTLY to the
    // estimate, because many OpenAI-compatible/local servers omit usage entirely and streaming usage is only
    // present when the server emits it (OpenAI: stream_options.include_usage) — which we don't force, since some
    // servers reject unknown params. result.PromptTokens already holds the prompt estimate computed in Begin().
    private static void ApplyTokenCounts(LlmCallResult result, UsageDetails? usage, string text)
    {
        result.PromptTokens = (int?)usage?.InputTokenCount ?? result.PromptTokens;
        result.CompletionTokens = (int?)usage?.OutputTokenCount ?? TokenEstimator.Estimate(text);
        result.TotalTokens = (int?)usage?.TotalTokenCount ?? (result.PromptTokens + result.CompletionTokens);
        result.CachedPromptTokens = ReadCachedPromptTokens(usage, result.PromptTokens);
    }

    /// <summary>
    /// Số token prompt được phục vụ từ cache (<c>prompt_tokens_details.cached_tokens</c> phía OpenAI).
    /// KHÔNG có ước lượng thay thế: cache là chuyện phía provider, đoán ra một con số là bịa ra một khoản
    /// giảm giá không tồn tại — không có số thì 0, và chi phí tính theo giá input đầy đủ (đúng bằng hành vi
    /// trước khi có cột này).
    /// Kẹp trong [0, PromptTokens] vì phần cache nằm TRONG prompt: một endpoint lạ trả số lớn hơn sẽ đẩy
    /// phần input phải trả tiền xuống ÂM và làm lệch mọi bản tổng cộng dồn từ đó.
    /// </summary>
    private static int ReadCachedPromptTokens(UsageDetails? usage, int promptTokens)
        // Math.Clamp NÉM khi min > max, nên cận trên phải tự kẹp về >= 0 trước: một promptTokens âm (endpoint
        // trả usage vô lý) sẽ biến một dòng thống kê thành exception giữa đường ống gọi model.
        => usage?.CachedInputTokenCount is { } cached ? (int)Math.Clamp(cached, 0, Math.Max(promptTokens, 0)) : 0;

    // Single place the budget breaker is consulted (both overrides call this first). No-op when no guard is
    // wired (eval, unit tests) so behaviour is unchanged unless enabled.
    private Task EnsureWithinBudgetAsync(CancellationToken cancellationToken) =>
        _options.BudgetGuard?.EnsureWithinBudgetAsync(_context.ProjectId, cancellationToken) ?? Task.CompletedTask;

    private async Task FailAsync(LlmCallResult result, Stopwatch stopwatch, int step, Exception ex)
    {
        stopwatch.Stop();
        result.DurationMs = stopwatch.ElapsedMilliseconds;
        result.IsSuccess = false;
        switch (ex)
        {
            // Non-2xx from the API (incl. OpenAI-compatible servers). Keep the short message in the
            // DB-persisted, UI-visible fields; the full exception is surfaced by the caller's logger.
            case ClientResultException api:
                result.HttpStatusCode = api.Status;
                result.ErrorMessage = $"API error: {api.Status} ({Target})";
                result.Content = $"API error: {api.Status} ({Target})\n\n{api.Message}";
                result.ResponseText = api.Message;
                break;
            // Our own deadline fired (stalled/slow stream).
            case OperationCanceledException:
                result.ErrorMessage = $"LLM request timed out after {_options.RequestTimeoutSeconds}s ({Target}).";
                result.Content = result.ErrorMessage;
                result.ResponseText = result.ErrorMessage;
                break;
            default:
                // Nguyên nhân THẬT nằm ở các lớp InnerException, không ở lớp ngoài: thông điệp lớp ngoài của
                // một lỗi mạng luôn là câu chung chung "An error occurred while sending the request", nhân
                // lên 4 lần bởi chính sách retry. In cả chuỗi ra đây vì đây là chỗ duy nhất còn cầm được
                // exception — xuống tới bong bóng ⚠️ và call log thì chỉ còn lại chuỗi này.
                var detail = LlmExceptionDetail.Describe(ex);
                // Request không tới được model (kết nối đứt/bị chặn) — xem LlmCallResult.TransportFailure.
                result.TransportFailure = LlmExceptionDetail.IsTransportFailure(ex);
                // Lỗi mạng đứng một mình là câu đố: người dùng đọc "Retry failed after 4 tries" không biết
                // phải làm gì, còn người sửa không biết nhìn vào đâu.
                result.ErrorMessage = result.TransportFailure ? TransportFailureAdvice(result, detail) : $"{detail} ({Target})";
                result.Content = result.ErrorMessage;
                result.ResponseText = detail;
                break;
        }
        result.CompletionTokens = TokenEstimator.Estimate(result.Content);
        result.TotalTokens = result.PromptTokens + result.CompletionTokens;

        _options.OnProgress?.Invoke("error", "Lời gọi LLM thất bại.", result.ErrorMessage);
        await CompleteAsync(result, step).ConfigureAwait(false);
    }

    // Gói tin nhỏ hơn ngần này thì KHÔNG thể là chuyện "vượt trần body của gateway" — đừng nhắc tới nó.
    // Một dòng gợi ý sai hướng trong thông báo lỗi không phải là vô hại: nó là thứ người ta làm theo đầu
    // tiên, và làm theo xong thì lỗi vẫn còn nguyên mà thời gian thì mất rồi.
    private const long BodyLimitSuspicionBytes = 1024 * 1024;

    /// <summary>
    /// Câu chỉ đường cho một lời gọi chết ở tầng truyền tải. Việc ĐẦU TIÊN cần làm luôn là bấm "Test
    /// Connection" ở trang Models: nó chạy đúng đường dây này với một request tí hon, nên nếu nó cũng hỏng
    /// thì sự cố nằm ở CẤU HÌNH MODEL (endpoint chết, agent gắn nhầm model, mạng chặn host, proxy sai) chứ
    /// không dính dáng gì tới nội dung lượt chat — cắt gọn được cả nhánh phỏng đoán sai.
    /// </summary>
    private string TransportFailureAdvice(LlmCallResult result, string detail)
    {
        var sizeNote = result.ApproxRequestBytes >= BodyLimitSuspicionBytes
            ? $" Gói tin lượt này ~{FormatBytes(result.ApproxRequestBytes)} — cũng có thể đã vượt trần body "
              + "của gateway/proxy đứng trước endpoint (hạ Llm:SourceUpload:MaxTotalImageBytes)."
            : string.Empty;

        return $"Không gửi được request tới {Target} — endpoint chưa từng trả lời (lỗi kết nối, gói tin lượt "
            + $"này ~{FormatBytes(result.ApproxRequestBytes)}). Vào trang Models, bấm \"Test Connection\" cho "
            + "model này: nếu nút đó cũng lỗi thì vấn đề nằm ở cấu hình model chứ không phải nội dung lượt "
            + "chat — kiểm tra endpoint có đang chạy không, agent có đang gắn đúng model không, mạng có chặn "
            + $"host đó không, và cấu hình Llm:Proxy có đúng không.{sizeNote} Nguyên nhân từ hệ thống: {detail}";
    }

    /// <summary>
    /// "model @ host" của lời gọi vừa hỏng. Bắt buộc phải có trong MỌI thông điệp lỗi: một agent có thể bị
    /// gắn nhầm sang model khác trên trang Agents/Models, và khi đó triệu chứng là "chỗ này chạy, chỗ kia
    /// lỗi" — không thể lần ra nếu thông báo không nói nó vừa gọi tới ĐÂU. Chỉ lấy host, không lấy nguyên
    /// URL: bong bóng lỗi hiện cho mọi người dùng, còn đường dẫn/tham số của endpoint là chuyện quản trị.
    /// </summary>
    private string Target
    {
        get
        {
            var host = OpenAiCompatibility.HostOf(_model.Endpoint);
            return string.IsNullOrWhiteSpace(host) ? _model.ModelId : $"{_model.ModelId} @ {host}";
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B",
    };

    // A failed call ends the run on the agent path; the chat/eval paths keep the failure on the result and
    // let the caller decide (fallback parse, error turn in the UI).
    private void ThrowIfConfigured(LlmCallResult result, Exception ex)
    {
        if (_options.ThrowOnFailure)
            throw new InvalidOperationException($"LLM call failed: {result.ErrorMessage}", ex);
    }

    // Persist the call log (one place, identical shape for both paths) then surface the result.
    private async Task CompleteAsync(LlmCallResult result, int step)
    {
        await _logger.LogAsync(_context.ProjectId, _context.Agent, result, step, _context.Purpose, _context.WorkflowRunId).ConfigureAwait(false);
        _options.OnCompleted?.Invoke(result);
    }

    private string BudgetLabel(int step) =>
        _options.MaxSteps <= 0 ? step.ToString()
        : step <= _options.MaxSteps ? $"{step}/{_options.MaxSteps}"
        : $"{step}/{_options.HardCap} (chạy thêm để hoàn tất)";

    private readonly record struct CallState(
        int Step, IList<ChatMessage> Messages, ChatOptions Options, LlmCallResult Result, Stopwatch Stopwatch,
        int MaxTokens);
}
