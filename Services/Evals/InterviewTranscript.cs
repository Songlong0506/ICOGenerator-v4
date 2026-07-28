using System.Text;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Services.Evals;

/// <summary>Một lượt trong phỏng vấn mô phỏng: BA hỏi (kèm gợi ý) rồi persona trả lời.</summary>
public sealed record InterviewTurn(string BaMessage, IReadOnlyList<string> Suggestions, string UserReply);

/// <summary>
/// Các con số ĐO ĐƯỢC của một cuộc phỏng vấn mô phỏng — phần không cần LLM chấm.
///
/// <para>
/// Judge trả lời được "cuộc phỏng vấn có hiểu đúng bài toán không", nhưng những thứ dưới đây thì một
/// judge chấm điểm 1–5 hay bỏ sót và cũng không nhất quán giữa các lần chạy: phỏng vấn có TỚI ĐÍCH
/// không, tốn bao nhiêu lượt, và có vi phạm các quy tắc mà prompt tự đặt ra cho mình không (mỗi lượt một
/// câu hỏi, hỏi thì phải kèm gợi ý). Đo tất định ở đây thì so hai lần chạy là so được thật.
/// </para>
/// </summary>
public sealed record InterviewMetrics(
    int Turns,
    bool ReachedWriteRequirement,
    int TurnsWithMultipleQuestions,
    int QuestionTurnsWithoutSuggestions)
{
    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- Số lượt hỏi–đáp: {Turns}");
        sb.AppendLine(ReachedWriteRequirement
            ? "- Kết thúc: BA đã mời bấm \"Write Requirement\" (phỏng vấn tới đích)"
            : "- Kết thúc: CHẠM TRẦN số lượt mà BA vẫn chưa mời bấm \"Write Requirement\" (phỏng vấn chưa tới đích)");
        sb.AppendLine($"- Lượt hỏi DỒN nhiều câu (vi phạm quy tắc mỗi lượt một câu hỏi): {TurnsWithMultipleQuestions}");
        sb.Append($"- Lượt có hỏi nhưng KHÔNG kèm đáp án gợi ý: {QuestionTurnsWithoutSuggestions}");
        return sb.ToString();
    }
}

/// <summary>
/// Bản ghi một cuộc phỏng vấn mô phỏng + cách đo nó. Tách khỏi
/// <see cref="EvalRunnerService"/> để đo được bằng unit test mà không cần model thật.
/// </summary>
public static class InterviewTranscript
{
    /// <summary>Ký tự kết thúc câu hỏi dùng để đếm số câu hỏi trong một lượt (cả dạng đầy đủ lẫn nửa chiều rộng).</summary>
    private static readonly char[] QuestionMarks = ['?', '？'];

    public static InterviewMetrics Measure(IReadOnlyList<InterviewTurn> turns)
    {
        var multiQuestion = 0;
        var questionWithoutSuggestions = 0;

        foreach (var turn in turns)
        {
            var questions = CountQuestions(turn.BaMessage);
            if (questions > 1)
                multiQuestion++;
            if (questions > 0 && turn.Suggestions.Count == 0)
                questionWithoutSuggestions++;
        }

        var reached = turns.Count > 0 && RequirementReadinessGate.IsWriteRequirementInvite(turns[^1].BaMessage);
        return new InterviewMetrics(turns.Count, reached, multiQuestion, questionWithoutSuggestions);
    }

    public static int CountQuestions(string? message) =>
        (message ?? string.Empty).Count(c => QuestionMarks.Contains(c));

    /// <summary>Transcript ở dạng markdown để judge đọc và để người xem kết quả eval đọc lại nguyên văn.</summary>
    public static string Render(IReadOnlyList<InterviewTurn> turns)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < turns.Count; i++)
        {
            sb.AppendLine($"**BA (lượt {i + 1}):** {turns[i].BaMessage}");
            if (turns[i].Suggestions.Count > 0)
                sb.AppendLine($"*(gợi ý: {string.Join(" · ", turns[i].Suggestions)})*");
            sb.AppendLine($"**Người dùng:** {turns[i].UserReply}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
