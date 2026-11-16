using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Requirements;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Evals;

/// <summary>
/// Một LOẠI lượt trọng tài kiểm toán được — mỗi mục đích của <see cref="InterviewJudge"/> một bản: biết kiểu
/// phán quyết của nó, cách đổi phán quyết thành <see cref="JudgeClaim"/>, và cách hỏi một model (ứng viên
/// hay trọng tài phân xử) bằng đúng đường structured output mà production dùng.
///
/// <para>
/// Thêm một trọng tài mới vào kiểm toán = thêm một dòng vào <see cref="All"/>. Không có <c>if (purpose == …)</c>
/// nào ở runner hay ở trang kết quả.
/// </para>
/// </summary>
public abstract class JudgeAuditSubject
{
    /// <summary>Mọi loại trọng tài kiểm toán được, theo thứ tự hiện trên form.</summary>
    public static readonly IReadOnlyList<JudgeAuditSubject> All = new JudgeAuditSubject[]
    {
        new Typed<TurnJudgeVerdict>(
            InterviewJudge.TurnPurpose,
            new[] { "repeats", "emptyChips", "yesNoChips", "userMentionsExistingSource", "asksForFile" },
            TurnClaims),
        new Typed<CoverageQuestionVerdict>(
            InterviewJudge.CoveragePurpose,
            new[] { "drop" },
            CoverageClaims),
    };

    public static JudgeAuditSubject? For(string? purpose) =>
        All.FirstOrDefault(s => string.Equals(s.Purpose, purpose, StringComparison.Ordinal));

    /// <summary>Mục đích trong AI Call Logs (<c>AgentModelCallLog.Purpose</c>).</summary>
    public abstract string Purpose { get; }

    /// <summary>Các trường của phán quyết, theo thứ tự hiện trong bảng điểm.</summary>
    public abstract IReadOnlyList<string> Fields { get; }

    /// <summary>Đọc phán quyết từ chữ model trả về. Null = không đọc được (ở production: phán quyết rỗng).</summary>
    public abstract IReadOnlyList<JudgeClaim>? ParseClaims(string? text);

    /// <summary>Gửi lại đúng đầu vào của lượt trọng tài cho <paramref name="model"/>.</summary>
    public abstract Task<(LlmCallResult Result, IReadOnlyList<JudgeClaim>? Claims)> AskAsync(
        ILlmClient llm, AiModel model, List<ChatMessage> messages, ModelCallLogContext log, CancellationToken cancellationToken);

    /// <summary>Hỏi trọng tài phân xử. Claims null = trả lời không đọc được.</summary>
    public abstract Task<(LlmCallResult Result, IReadOnlyList<JudgeClaim>? Claims, string? Notes)> ArbitrateAsync(
        ILlmClient llm, AiModel model, List<ChatMessage> messages, ModelCallLogContext log, CancellationToken cancellationToken);

    // ── Đổi phán quyết thành khẳng định ──────────────────────────────────────────────────────────────
    // Đọc theo đúng kiểu KHÔNG TIN của các chốt chặn: số thứ tự ≤ 0, chip rỗng bị bỏ; trùng thì gộp.

    internal static IEnumerable<JudgeClaim> TurnClaims(TurnJudgeVerdict v)
    {
        foreach (var n in v.Repeats.Where(n => n > 0).Distinct())
            yield return new("repeats", $"Q{n}");
        foreach (var group in v.EmptyChips.Where(g => g.Question > 0))
            foreach (var chip in group.Chips.Select(OneLine).Where(c => c.Length > 0).Distinct())
                yield return new("emptyChips", $"Q{group.Question} · {chip}");
        foreach (var n in v.YesNoChips.Where(n => n > 0).Distinct())
            yield return new("yesNoChips", $"Q{n}");
        if (v.UserMentionsExistingSource)
            yield return new("userMentionsExistingSource", JudgeClaim.True);
        if (v.AsksForFile)
            yield return new("asksForFile", JudgeClaim.True);
    }

    // Lý do (answered/hollow/not-a-question) không đi vào khẳng định: guard xoá câu hỏi như nhau với mọi lý do
    // hợp lệ, nên hai phán quyết cùng xoá Q2 với hai lý do khác nhau có CÙNG hiệu lực.
    internal static IEnumerable<JudgeClaim> CoverageClaims(CoverageQuestionVerdict v) =>
        v.Drop.Where(d => d.Question > 0).Select(d => d.Question).Distinct().Select(n => new JudgeClaim("drop", $"Q{n}"));

    private static string OneLine(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class Typed<TVerdict> : JudgeAuditSubject where TVerdict : class
    {
        private readonly Func<TVerdict, IEnumerable<JudgeClaim>> _claims;

        public Typed(string purpose, IReadOnlyList<string> fields, Func<TVerdict, IEnumerable<JudgeClaim>> claims)
        {
            Purpose = purpose;
            Fields = fields;
            _claims = claims;
        }

        public override string Purpose { get; }
        public override IReadOnlyList<string> Fields { get; }

        public override IReadOnlyList<JudgeClaim>? ParseClaims(string? text) =>
            LlmJson.TryDeserialize<TVerdict>(text) is { } verdict ? _claims(verdict).ToList() : null;

        public override async Task<(LlmCallResult Result, IReadOnlyList<JudgeClaim>? Claims)> AskAsync(
            ILlmClient llm, AiModel model, List<ChatMessage> messages, ModelCallLogContext log, CancellationToken cancellationToken)
        {
            var (result, value) = await llm.ChatStructuredAsync<TVerdict>(model, messages, log, cancellationToken: cancellationToken);
            if (!result.IsSuccess)
                return (result, null);

            // Cùng đường lùi với InterviewJudge: không có Value thì bóc JSON từ văn xuôi.
            var verdict = value ?? LlmJson.TryDeserialize<TVerdict>(result.Content);
            return (result, verdict == null ? null : _claims(verdict).ToList());
        }

        public override async Task<(LlmCallResult Result, IReadOnlyList<JudgeClaim>? Claims, string? Notes)> ArbitrateAsync(
            ILlmClient llm, AiModel model, List<ChatMessage> messages, ModelCallLogContext log, CancellationToken cancellationToken)
        {
            var (result, value) = await llm.ChatStructuredAsync<JudgeAuditArbiterReply<TVerdict>>(
                model, messages, log, cancellationToken: cancellationToken);
            if (!result.IsSuccess)
                return (result, null, null);

            var reply = value ?? LlmJson.TryDeserialize<JudgeAuditArbiterReply<TVerdict>>(result.Content);
            return reply?.Verdict is { } verdict
                ? (result, _claims(verdict).ToList(), reply.Notes?.Trim())
                : (result, null, null);
        }
    }

    /// <summary>
    /// Đầu vào cho trọng tài phân xử: luật mà lượt trọng tài đã nhận (chính system prompt GỐC trong log — tức
    /// đúng phiên bản prompt lúc đó), đầu vào nó đã nhận, và các phán quyết cần đối chiếu dán nhãn A/B — không
    /// lộ model nào phán, để tên model không nghiêng cán cân.
    /// </summary>
    public static string RenderArbiterInput(
        JudgeLogRequest.Input original, IReadOnlyList<string> fields,
        IReadOnlyList<IReadOnlyList<JudgeClaim>?> verdicts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Luật của lượt trọng tài (system prompt nó đã nhận)");
        sb.AppendLine(original.System.Trim());
        sb.AppendLine();
        sb.AppendLine("## Đầu vào lượt trọng tài đã nhận");
        sb.AppendLine(original.User.Trim());
        sb.AppendLine();
        sb.AppendLine("## Các phán quyết cần đối chiếu");
        for (var i = 0; i < verdicts.Count; i++)
        {
            sb.AppendLine();
            sb.Append("### Phán quyết ").AppendLine(((char)('A' + i)).ToString());
            if (verdicts[i] is not { } claims)
            {
                sb.AppendLine("(trả lời không đọc được — ở hệ thống thật tương đương phán quyết rỗng)");
                continue;
            }
            foreach (var field in fields)
            {
                var keys = claims.Where(c => c.Field == field).Select(c => c.Key).ToList();
                sb.Append("- ").Append(field).Append(": ")
                  .AppendLine(keys.Count == 0 ? "(không có)" : string.Join(" | ", keys));
            }
        }
        return sb.ToString();
    }
}
