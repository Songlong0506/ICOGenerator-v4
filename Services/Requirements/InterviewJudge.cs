using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// LƯỢT TRỌNG TÀI của buổi phỏng vấn: một lời gọi LLM riêng, ngắn, chỉ để PHÁN — không viết lại gì.
///
/// <para>
/// <b>Vì sao tồn tại.</b> Các chốt chặn của buổi phỏng vấn từng tự trả lời những câu hỏi về NGHĨA bằng bảng
/// cụm từ tiếng Việt: "câu này hỏi rồi chưa" (cụm xin gật, cụm hỏi-vét), "chip này có chở câu trả lời
/// không" (đầu mê-ta + đuôi "khác"), "câu hỏi tồn đọng này đã được trả lời chưa" (tỷ lệ từ trùng sau khi
/// bỏ hư từ), "người dùng có nhắc tới một file đang dùng không". Bảng nào cũng lớn dần theo từng ca lọt
/// lưới, và tất cả cùng câm — hoặc tệ hơn, chặn oan — ngay khi hội thoại không phải tiếng Việt, trong khi
/// prompt BA dặn trả lời "đúng ngôn ngữ của người dùng". Nghĩa thì hỏi một model; code chỉ giữ lại phần
/// HÌNH DẠNG (khớp nguyên văn, đếm chip, thứ tự guard).
/// </para>
///
/// <para>
/// <b>Hai điểm gọi, ba prompt.</b> <see cref="JudgeTurnAsync"/> soát lượt BA vừa soạn, ngay trước các
/// chốt chặn của <c>BAChatService.ShapeTurnAsync</c>. <see cref="JudgeOpenQuestionsAsync"/> soát danh sách
/// câu hỏi tồn đọng ngay sau lượt chắt lọc bản đồ, TRƯỚC <see cref="CoveragePendingGuard"/> — đứng sau thì
/// một dòng <c>[RÕ]</c> đã bị hạ vì một câu hỏi sắp bị loại. Không gộp được hai điểm làm một: lúc bản đồ
/// được chắt thì lượt BA chưa tồn tại.
/// </para>
///
/// <para>
/// <b>Lượt BA là HAI lời gọi song song.</b> Phán quyết <c>repeats</c> (<see cref="TurnPurpose"/>) phải so
/// NGHĨA với tới <see cref="MaxAskedQuestions"/> câu đã hỏi, đọc câu người dùng đáp từng câu để biết thông
/// tin đã có chưa, và phân biệt các ca tinh tế (hỏi-vét đổi vế liệt kê, cùng khuôn câu khác chủ thể) — nên
/// luôn chạy trên model BA. Bốn phán quyết còn lại
/// (<see cref="SignalPurpose"/>: chip rỗng, cặp chip có/không, người dùng nhắc file, BA xin file) chỉ đọc lượt
/// hiện tại — nên chạy trên model trọng tài và KHÔNG gửi sổ "đã hỏi", phần nặng nhất của đầu vào. Hai lời gọi
/// hỏng độc lập: nửa nào lỗi thì chỉ nửa đó rỗng.
/// </para>
///
/// <para>
/// <b>Fail-open, và chỉ được XOÁ.</b> Lời gọi lỗi, bị từ chối, hoặc trả về thứ không đọc được ⇒ phán quyết
/// rỗng ⇒ không chốt chặn nào động tay. Cái giá là đúng hành vi khi chưa có phanh: một câu hỏi lại lọt lên
/// màn hình, một chip "Khác" còn nguyên. Chiều ngược lại — chặn oan một câu hỏi mới vì trọng tài hỏng —
/// đắt hơn nhiều, nên không có đường nào ở đây ném ngoại lệ ra khỏi lượt chat.
/// </para>
///
/// <para>
/// <b>Model.</b> Mặc định dùng model của agent BA, như mọi lượt phụ khác của buổi phỏng vấn. Suất
/// <see cref="ModelSlot.Judge"/> đã khai (màn AI Models → "Model theo vai trò") thì CHỈ <see cref="SignalPurpose"/>
/// đi model đó — xem <see cref="ModelSlotResolver"/>. <see cref="TurnPurpose"/> và <see cref="CoveragePurpose"/> LUÔN đi model BA:
/// phán sai ở hai chỗ này là xoá oan một câu hỏi mới (ca thật của <see cref="CoveragePurpose"/>: một model
/// trọng tài rẻ xoá cả hai câu hỏi đúng distiller vừa viết, trái với chính ví dụ trong prompt). Model trọng tài lỗi HẠ TẦNG (lời gọi không thành, ném lỗi) thì gọi lại một lần
/// bằng model BA: cấu hình sai endpoint của model trọng tài không được phép âm thầm tắt hết phanh. Trả lời
/// sai hay không đọc được thì KHÔNG gọi lại — đó là chất lượng của model trọng tài, thứ
/// <c>JudgeAuditRunnerService</c> đo, chứ không phải sự cố.
/// </para>
///
/// <para>
/// Ba lời gọi ghi vào AI Call Logs dưới mục đích <see cref="TurnPurpose"/>, <see cref="SignalPurpose"/> và
/// <see cref="CoveragePurpose"/> — chính các dòng log này là nguyên liệu của kiểm toán trọng tài.
/// </para>
/// </summary>
public class InterviewJudge
{
    public const string TurnPromptPath = "BusinessAnalyst/turn-judge.v1.md";
    public const string SignalPromptPath = "BusinessAnalyst/turn-signal-judge.v1.md";
    public const string CoveragePromptPath = "BusinessAnalyst/coverage-question-judge.v1.md";

    /// <summary>Phán quyết <c>repeats</c> — giữ tên mục đích cũ để AI Call Logs và kiểm toán đọc liền mạch.</summary>
    public const string TurnPurpose = "BATurnJudge";

    /// <summary>Chip rỗng, cặp chip có/không, người dùng nhắc file, BA xin file.</summary>
    public const string SignalPurpose = "BATurnSignalJudge";
    public const string CoveragePurpose = "BACoverageQuestionJudge";

    /// <summary>
    /// Trần số câu đã hỏi gửi cho trọng tài. Lấy các LƯỢT mới nhất (cắt theo lượt để câu đáp không bị tách
    /// khỏi câu hỏi của nó): hỏi lại gần như luôn là hỏi lại một câu của vài lượt vừa qua, và danh sách này
    /// đi vào prompt ở mọi lượt chat.
    /// </summary>
    public const int MaxAskedQuestions = 80;

    /// <summary>
    /// Trần ký tự của MỖI lượt người dùng trong sổ gửi trọng tài. Câu đáp thường là vài dòng; trần chỉ để
    /// một lần dán nguyên văn dài (một bảng, một email) không thổi phồng lời gọi chạy ở mọi lượt. Cắt mất
    /// câu trả lời thì trọng tài thấy "chưa đáp" và để câu hỏi đi qua — chiều lỗi rẻ hơn.
    /// </summary>
    public const int MaxReplyChars = 1500;

    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly ModelSlotResolver _slots;
    private readonly IServiceScopeFactory? _scopeFactory;

    /// <param name="scopeFactory">
    /// Có thì lời gọi <see cref="SignalPurpose"/> chạy trong một DI scope riêng, SONG SONG với lời gọi
    /// <see cref="TurnPurpose"/>: <see cref="ILlmClient"/> ghi AI Call Logs qua <c>AppDbContext</c> scoped, mà
    /// DbContext không chịu hai thao tác cùng lúc. Không có (unit test dựng tay) ⇒ hai lời gọi tuần tự.
    /// </param>
    public InterviewJudge(ILlmClient llm, PromptTemplateService prompts, ModelSlotResolver slots, IServiceScopeFactory? scopeFactory = null)
    {
        _llm = llm;
        _prompts = prompts;
        _slots = slots;
        _scopeFactory = scopeFactory;
    }

    /// <summary>Một câu hỏi của lượt BA đem cho trọng tài xét, kèm các chip của riêng nó.</summary>
    public sealed record TurnQuestion(string Text, IReadOnlyList<string> Chips);

    /// <summary>
    /// Soát lượt BA vừa soạn. <paramref name="questions"/> theo đúng thứ tự mà caller sẽ đọc lại số thứ tự
    /// trong phán quyết (câu đầu là số 1). Mỗi nửa tự bỏ qua khi không có gì để phán: <c>repeats</c> khi
    /// lượt không hỏi câu nào hoặc sổ "đã hỏi" còn rỗng (caller cũng bỏ qua <c>repeats</c> khi đó, nên lời
    /// gọi chỉ tốn tiền); bốn phán quyết dễ khi không có câu hỏi lẫn lượt người dùng.
    /// </summary>
    public virtual async Task<TurnJudgeVerdict> JudgeTurnAsync(
        Guid projectId,
        IReadOnlyList<AskedQuestionHistory.AskedTurn> askedBefore,
        string? lastUserMessage,
        IReadOnlyList<TurnQuestion> questions,
        bool summaryCheck,
        Agent ba,
        AiModel model,
        CancellationToken cancellationToken = default)
    {
        var judgeRepeats = questions.Count > 0
                           && askedBefore.Any(t => t.Questions.Any(q => !string.IsNullOrWhiteSpace(q)));
        var judgeSignals = questions.Count > 0 || !string.IsNullOrWhiteSpace(lastUserMessage);

        // Dựng cả hai đầu vào TRƯỚC khi rẽ nhánh: PromptTemplateService đọc DB override qua cache, không
        // đem nó vào luồng song song.
        var repeatMessages = judgeRepeats
            ? new List<ChatMessage>
            {
                new(ChatRole.System, _prompts.Get(TurnPromptPath)),
                new(ChatRole.User, RenderRepeatInput(askedBefore, questions, summaryCheck))
            }
            : null;
        var signalMessages = judgeSignals
            ? new List<ChatMessage>
            {
                new(ChatRole.System, _prompts.Get(SignalPromptPath)),
                new(ChatRole.User, RenderSignalInput(lastUserMessage, questions))
            }
            : null;

        // Tra suất trọng tài TRƯỚC khi rẽ nhánh, cùng lý do: nó đọc DbContext của caller.
        var judgeModel = signalMessages == null ? model : await FindJudgeModelAsync(model, cancellationToken);

        // repeats: model BA, không lùi model nào. Bốn phán quyết dễ: model trọng tài, lỗi hạ tầng thì lùi về BA.
        var repeatTask = repeatMessages == null
            ? Task.FromResult<TurnRepeatVerdict?>(null)
            : CallOnModelAsync<TurnRepeatVerdict>(_llm, projectId, repeatMessages, TurnPurpose, ba, model, cancellationToken);

        Task<TurnSignalVerdict?> signalTask;
        if (signalMessages == null)
            signalTask = Task.FromResult<TurnSignalVerdict?>(null);
        else if (_scopeFactory == null)
        {
            await repeatTask;
            signalTask = CallAsync<TurnSignalVerdict>(_llm, projectId, signalMessages, SignalPurpose, ba, judgeModel, model, cancellationToken);
        }
        else
            signalTask = CallInOwnScopeAsync<TurnSignalVerdict>(projectId, signalMessages, SignalPurpose, ba, judgeModel, model, cancellationToken);

        await Task.WhenAll(repeatTask, signalTask);
        return TurnJudgeVerdict.Merge(repeatTask.Result, signalTask.Result);
    }

    /// <summary>
    /// Soát danh sách câu hỏi tồn đọng. <paramref name="questions"/> theo đúng thứ tự mà caller sẽ đọc lại
    /// số thứ tự trong phán quyết. Không có câu nào ⇒ không gọi model.
    /// <para>
    /// Chạy trên ĐÚNG <paramref name="model"/> (model BA), không xin suất <see cref="ModelSlot.Judge"/>: phán
    /// quyết này xoá câu hỏi khỏi cuộc phỏng vấn, và nó phải đọc được các phân biệt tinh tế (một bản đồ ghi
    /// "ai được tạo và sửa" chưa trả lời "ai được XOÁ") mà model trọng tài rẻ đã làm sai trên log thật.
    /// </para>
    /// </summary>
    public virtual async Task<CoverageQuestionVerdict> JudgeOpenQuestionsAsync(
        Guid projectId,
        IReadOnlyList<CoverageMapItem> items,
        IReadOnlyList<OpenQuestionEntry> questions,
        Agent ba,
        AiModel model,
        CancellationToken cancellationToken = default)
    {
        if (questions.Count == 0)
            return new CoverageQuestionVerdict();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get(CoveragePromptPath)),
            new(ChatRole.User, RenderCoverageInput(items, questions))
        };

        return await CallOnModelAsync<CoverageQuestionVerdict>(_llm, projectId, messages, CoveragePurpose, ba, model, cancellationToken)
               ?? new CoverageQuestionVerdict();
    }

    /// <summary>
    /// Model của suất <see cref="ModelSlot.Judge"/>, chưa khai thì <paramref name="baModel"/>. Fail-open như mọi
    /// đường khác ở đây: đọc cấu hình suất lỗi thì chạy model BA, không làm hỏng lượt chat.
    /// </summary>
    private async Task<AiModel> FindJudgeModelAsync(AiModel baModel, CancellationToken cancellationToken)
    {
        try
        {
            return await _slots.FindAsync(ModelSlot.Judge, cancellationToken) ?? baModel;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return baModel;
        }
    }

    /// <summary>
    /// Lời gọi trên <paramref name="judgeModel"/> (model của suất trọng tài, hoặc chính model BA khi suất chưa
    /// khai); lỗi hạ tầng thì gọi lại một lần bằng <paramref name="baModel"/>.
    /// </summary>
    private static async Task<T?> CallAsync<T>(
        ILlmClient llm, Guid projectId, List<ChatMessage> messages, string purpose, Agent ba, AiModel judgeModel,
        AiModel baModel, CancellationToken cancellationToken) where T : class
    {
        var outcome = await CallOnceAsync<T>(llm, projectId, messages, purpose, ba, judgeModel, cancellationToken);

        if (outcome.Failed && judgeModel.Id != baModel.Id)
            outcome = await CallOnceAsync<T>(llm, projectId, messages, purpose, ba, baModel, cancellationToken);

        return outcome.Value;
    }

    /// <summary>Lời gọi trên đúng <paramref name="model"/>, không lùi model nào.</summary>
    private static async Task<T?> CallOnModelAsync<T>(
        ILlmClient llm, Guid projectId, List<ChatMessage> messages, string purpose, Agent ba, AiModel model,
        CancellationToken cancellationToken) where T : class
        => (await CallOnceAsync<T>(llm, projectId, messages, purpose, ba, model, cancellationToken)).Value;

    /// <summary>
    /// <see cref="CallAsync{T}"/> trên một <see cref="ILlmClient"/> của DI scope riêng (DbContext riêng cho
    /// call log và trần ngân sách). <paramref name="ba"/>/<paramref name="baModel"/> thuộc context của caller
    /// nhưng chỉ được ĐỌC ở đây, không gắn vào context mới.
    /// </summary>
    private async Task<T?> CallInOwnScopeAsync<T>(
        Guid projectId, List<ChatMessage> messages, string purpose, Agent ba, AiModel judgeModel, AiModel baModel,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            await using var scope = _scopeFactory!.CreateAsyncScope();
            var llm = scope.ServiceProvider.GetRequiredService<ILlmClient>();
            return await CallAsync<T>(llm, projectId, messages, purpose, ba, judgeModel, baModel, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Fail-open như CallOnceAsync: dựng scope hỏng cũng không được làm hỏng lượt chat.
            return null;
        }
    }

    /// <summary>
    /// Một lời gọi trọng tài. <c>Failed</c> = sự cố hạ tầng (lời gọi không thành / ném lỗi), phân biệt với
    /// "thành công nhưng không đọc được" — chỉ ca đầu đáng gọi lại bằng model khác.
    /// </summary>
    private static async Task<(T? Value, bool Failed)> CallOnceAsync<T>(
        ILlmClient llm, Guid projectId, List<ChatMessage> messages, string purpose, Agent ba, AiModel model,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            var (result, value) = await llm.ChatStructuredAsync<T>(
                model, messages, new ModelCallLogContext(projectId, ba, purpose),
                cancellationToken: cancellationToken);

            if (!result.IsSuccess)
                return (null, true);

            // Model không nhận response_format ⇒ Value null, bóc JSON từ văn xuôi như mọi service khác.
            return (value ?? LlmJson.TryDeserialize<T>(result.Content), false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Fail-open: trọng tài hỏng không được phép làm hỏng lượt chat. Lỗi của lời gọi đã nằm trong
            // AI Call Logs (middleware ghi log bọc quanh mọi lời gọi), nên không nuốt mất dấu vết nào.
            return (null, true);
        }
    }

    /// <summary>
    /// Đầu vào phán <c>repeats</c>: sổ "đã hỏi" THEO LƯỢT, mỗi lượt kèm các câu người dùng đáp sau nó, rồi
    /// CHỈ chữ của các câu cần xét — chip không đổi được câu nào thành câu mới hay câu cũ.
    ///
    /// <para>
    /// Câu đáp là thứ phân biệt "hỏi lại điều đã được trả lời" với "hỏi lại câu người dùng bỏ qua" — xem
    /// <see cref="AskedQuestionHistory"/>. Lượt người dùng mới nhất không có khối riêng: nó là câu đáp của
    /// lượt hỏi cuối trong sổ.
    /// </para>
    /// </summary>
    internal static string RenderRepeatInput(
        IReadOnlyList<AskedQuestionHistory.AskedTurn> askedBefore,
        IReadOnlyList<TurnQuestion> questions, bool summaryCheck)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Các lượt BA đã hỏi và câu người dùng đáp");
        var number = 0;
        var turnNumber = 0;
        foreach (var turn in LatestTurns(askedBefore))
        {
            sb.AppendLine();
            sb.Append("### Lượt hỏi ").AppendLine((++turnNumber).ToString());
            foreach (var question in turn.Questions)
                sb.Append(++number).Append(". ").AppendLine(OneLine(question));
            if (turn.Replies.Count == 0)
                sb.AppendLine("→ Người dùng: (chưa đáp)");
            foreach (var reply in turn.Replies)
                sb.Append("→ Người dùng: ").AppendLine(Clip(OneLine(reply)));
        }

        sb.AppendLine();
        sb.AppendLine("## Câu hỏi của lượt BA cần xét");
        if (summaryCheck)
            sb.AppendLine("summaryCheck: true");
        for (var i = 0; i < questions.Count; i++)
            sb.Append('Q').Append(i + 1).Append(": ").AppendLine(OneLine(questions[i].Text));

        return sb.ToString();
    }

    /// <summary>Đầu vào bốn phán quyết dễ: lượt người dùng, câu hỏi kèm chip — KHÔNG có sổ "đã hỏi".</summary>
    internal static string RenderSignalInput(string? lastUserMessage, IReadOnlyList<TurnQuestion> questions)
    {
        var sb = new StringBuilder();

        AppendUserTurn(sb, lastUserMessage);

        sb.AppendLine();
        sb.AppendLine("## Câu hỏi của lượt BA cần xét");
        if (questions.Count == 0)
            sb.AppendLine("(lượt này không hỏi câu nào)");
        for (var i = 0; i < questions.Count; i++)
        {
            sb.Append('Q').Append(i + 1).Append(": ").AppendLine(OneLine(questions[i].Text));
            foreach (var chip in questions[i].Chips)
                sb.Append("  - chip: ").AppendLine(OneLine(chip));
        }

        return sb.ToString();
    }

    private static void AppendUserTurn(StringBuilder sb, string? lastUserMessage)
    {
        if (sb.Length > 0)
            sb.AppendLine();
        sb.AppendLine("## Lượt người dùng mới nhất");
        sb.AppendLine(string.IsNullOrWhiteSpace(lastUserMessage) ? "(không có)" : lastUserMessage.Trim());
    }

    internal static string RenderCoverageInput(
        IReadOnlyList<CoverageMapItem> items, IReadOnlyList<OpenQuestionEntry> questions)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Bản đồ bao phủ");
        foreach (var item in items)
        {
            sb.Append("- ").Append(item.Label).Append(": ").AppendLine(item.Status);
            foreach (var known in item.Known.Where(k => !string.IsNullOrWhiteSpace(k)))
                sb.Append("  · ").AppendLine(OneLine(known));
        }

        sb.AppendLine();
        sb.AppendLine("## Câu hỏi tồn đọng");
        for (var i = 0; i < questions.Count; i++)
        {
            sb.Append('Q').Append(i + 1)
                .Append(" [nhóm: ").Append(string.IsNullOrWhiteSpace(questions[i].Group) ? "(không rõ)" : questions[i].Group).Append("]: ")
                .AppendLine(OneLine(questions[i].Text));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Các lượt MỚI NHẤT của sổ mà tổng số câu không quá <see cref="MaxAskedQuestions"/>, cắt theo LƯỢT. Lượt
    /// cuối luôn được giữ (kể cả khi một mình nó vượt trần): đó là lượt người dùng vừa đáp.
    /// </summary>
    private static IEnumerable<AskedQuestionHistory.AskedTurn> LatestTurns(IReadOnlyList<AskedQuestionHistory.AskedTurn> asked)
    {
        var start = asked.Count;
        var count = 0;
        while (start > 0)
        {
            var next = asked[start - 1].Questions.Count;
            if (start < asked.Count && count + next > MaxAskedQuestions)
                break;
            count += next;
            start--;
        }
        return asked.Skip(start);
    }

    private static string Clip(string text)
        => text.Length <= MaxReplyChars ? text : text[..MaxReplyChars] + " …(lược bớt)";

    private static string OneLine(string? text)
        => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
