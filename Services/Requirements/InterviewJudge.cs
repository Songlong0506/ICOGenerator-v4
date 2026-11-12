using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.Extensions.AI;

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
/// <b>Hai điểm gọi, hai prompt.</b> <see cref="JudgeTurnAsync"/> soát lượt BA vừa soạn, ngay trước các
/// chốt chặn của <c>BAChatService.ShapeTurnAsync</c>. <see cref="JudgeOpenQuestionsAsync"/> soát danh sách
/// câu hỏi tồn đọng ngay sau lượt chắt lọc bản đồ, TRƯỚC <see cref="CoveragePendingGuard"/> — đứng sau thì
/// một dòng <c>[RÕ]</c> đã bị hạ vì một câu hỏi sắp bị loại. Không gộp được hai điểm làm một: lúc bản đồ
/// được chắt thì lượt BA chưa tồn tại.
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
/// Dùng chung model của agent BA, như mọi lượt phụ khác của buổi phỏng vấn (chắt lọc bản đồ, xếp chỗ bước
/// luồng). Hai lời gọi ghi vào AI Call Logs dưới mục đích <see cref="TurnPurpose"/> và
/// <see cref="CoveragePurpose"/>.
/// </para>
/// </summary>
public class InterviewJudge
{
    public const string TurnPromptPath = "BusinessAnalyst/turn-judge.v1.md";
    public const string CoveragePromptPath = "BusinessAnalyst/coverage-question-judge.v1.md";
    public const string TurnPurpose = "BATurnJudge";
    public const string CoveragePurpose = "BACoverageQuestionJudge";

    /// <summary>
    /// Trần số câu đã hỏi gửi cho trọng tài. Lấy các câu MỚI NHẤT: hỏi lại gần như luôn là hỏi lại một
    /// câu của vài lượt vừa qua, và danh sách này đi vào prompt ở mọi lượt chat.
    /// </summary>
    public const int MaxAskedQuestions = 80;

    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public InterviewJudge(ILlmClient llm, PromptTemplateService prompts)
    {
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>Một câu hỏi của lượt BA đem cho trọng tài xét, kèm các chip của riêng nó.</summary>
    public sealed record TurnQuestion(string Text, IReadOnlyList<string> Chips);

    /// <summary>
    /// Soát lượt BA vừa soạn. <paramref name="questions"/> theo đúng thứ tự mà caller sẽ đọc lại số thứ tự
    /// trong phán quyết (câu đầu là số 1). Không có gì để xét ⇒ không gọi model.
    /// </summary>
    public virtual async Task<TurnJudgeVerdict> JudgeTurnAsync(
        Guid projectId,
        IReadOnlyList<string> askedBefore,
        string? lastUserMessage,
        IReadOnlyList<TurnQuestion> questions,
        bool summaryCheck,
        Agent ba,
        AiModel model,
        CancellationToken cancellationToken = default)
    {
        if (questions.Count == 0 && string.IsNullOrWhiteSpace(lastUserMessage))
            return new TurnJudgeVerdict();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get(TurnPromptPath)),
            new(ChatRole.User, RenderTurnInput(askedBefore, lastUserMessage, questions, summaryCheck))
        };

        return await CallAsync<TurnJudgeVerdict>(projectId, messages, TurnPurpose, ba, model, cancellationToken)
               ?? new TurnJudgeVerdict();
    }

    /// <summary>
    /// Soát danh sách câu hỏi tồn đọng. <paramref name="questions"/> theo đúng thứ tự mà caller sẽ đọc lại
    /// số thứ tự trong phán quyết. Không có câu nào ⇒ không gọi model.
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

        return await CallAsync<CoverageQuestionVerdict>(projectId, messages, CoveragePurpose, ba, model, cancellationToken)
               ?? new CoverageQuestionVerdict();
    }

    private async Task<T?> CallAsync<T>(
        Guid projectId, List<ChatMessage> messages, string purpose, Agent ba, AiModel model,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            var (result, value) = await _llm.ChatStructuredAsync<T>(
                model, messages, new ModelCallLogContext(projectId, ba, purpose),
                cancellationToken: cancellationToken);

            if (!result.IsSuccess)
                return null;

            // Model không nhận response_format ⇒ Value null, bóc JSON từ văn xuôi như mọi service khác.
            return value ?? LlmJson.TryDeserialize<T>(result.Content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Fail-open: trọng tài hỏng không được phép làm hỏng lượt chat. Lỗi của lời gọi đã nằm trong
            // AI Call Logs (middleware ghi log bọc quanh mọi lời gọi), nên không nuốt mất dấu vết nào.
            return null;
        }
    }

    internal static string RenderTurnInput(
        IReadOnlyList<string> askedBefore, string? lastUserMessage,
        IReadOnlyList<TurnQuestion> questions, bool summaryCheck)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Các câu BA đã hỏi ở những lượt trước");
        var asked = askedBefore.Count > MaxAskedQuestions
            ? askedBefore.Skip(askedBefore.Count - MaxAskedQuestions).ToList()
            : askedBefore;
        if (asked.Count == 0)
            sb.AppendLine("(chưa hỏi câu nào)");
        for (var i = 0; i < asked.Count; i++)
            sb.Append(i + 1).Append(". ").AppendLine(OneLine(asked[i]));

        sb.AppendLine();
        sb.AppendLine("## Lượt người dùng mới nhất");
        sb.AppendLine(string.IsNullOrWhiteSpace(lastUserMessage) ? "(không có)" : lastUserMessage.Trim());

        sb.AppendLine();
        sb.AppendLine("## Câu hỏi của lượt BA cần xét");
        if (summaryCheck)
            sb.AppendLine("summaryCheck: true");
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

    private static string OneLine(string? text)
        => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
