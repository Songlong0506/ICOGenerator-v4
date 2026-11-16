namespace ICOGenerator.Services.Evals;

/// <summary>
/// Chấm điểm một model trọng tài so với đáp án chuẩn, theo TỪNG trường của phán quyết.
///
/// <para>
/// Vì sao không là một con số "% đúng": phần lớn lượt có phán quyết rỗng (không hỏi lại, không chip rỗng),
/// nên một model luôn trả rỗng cũng "đúng" trên 90% lượt. Và hai loại sai có giá rất khác nhau — với
/// <c>repeats</c>/<c>emptyChips</c>/<c>drop</c>, <see cref="JudgeFieldScore.Extra"/> (phán thừa) là XOÁ OAN
/// một thứ người dùng cần thấy, còn <see cref="JudgeFieldScore.Missed"/> (phán sót) chỉ là để lọt về đúng
/// hành vi khi chưa có phanh. Thiết kế fail-open của <c>InterviewJudge</c> tồn tại chính vì cái giá lệch đó.
/// </para>
/// </summary>
public static class JudgeAuditScoring
{
    /// <summary>Một lượt đem chấm: khẳng định của model, đáp án chuẩn, và model có trả lời đọc được không.</summary>
    public sealed record Sample(IReadOnlyList<JudgeClaim> Subject, IReadOnlyList<JudgeClaim> Gold, bool Unreadable);

    public static JudgeSubjectScore Score(string label, IReadOnlyList<string> fields, IEnumerable<Sample> samples)
    {
        var list = samples.ToList();
        var fieldScores = fields.Select(field =>
        {
            int correct = 0, extra = 0, missed = 0, casesWithExtra = 0;
            foreach (var s in list)
            {
                var mine = s.Subject.Where(c => c.Field == field).ToHashSet();
                var gold = s.Gold.Where(c => c.Field == field).ToHashSet();
                var e = mine.Count(c => !gold.Contains(c));
                correct += mine.Count(gold.Contains);
                extra += e;
                missed += gold.Count(c => !mine.Contains(c));
                if (e > 0) casesWithExtra++;
            }
            return new JudgeFieldScore(field, correct, extra, missed, casesWithExtra);
        }).ToList();

        return new JudgeSubjectScore(
            label,
            Cases: list.Count,
            ExactCases: list.Count(s => JudgeClaim.SameSet(s.Subject, s.Gold)),
            CasesWithExtra: list.Count(s => s.Subject.Except(s.Gold).Any()),
            Unreadable: list.Count(s => s.Unreadable),
            fieldScores);
    }
}

/// <summary>Điểm của một trường. Precision/Recall null khi không có gì để chia (đừng hiện 0%).</summary>
public sealed record JudgeFieldScore(string Field, int Correct, int Extra, int Missed, int CasesWithExtra)
{
    /// <summary>Trong các khẳng định model đưa ra, bao nhiêu phần đúng. Thấp = hay xoá oan.</summary>
    public double? Precision => Correct + Extra == 0 ? null : (double)Correct / (Correct + Extra);

    /// <summary>Trong các khẳng định lẽ ra phải có, model bắt được bao nhiêu. Thấp = hay để lọt.</summary>
    public double? Recall => Correct + Missed == 0 ? null : (double)Correct / (Correct + Missed);
}

/// <summary>Điểm của một model trên một tập lượt.</summary>
public sealed record JudgeSubjectScore(
    string Label, int Cases, int ExactCases, int CasesWithExtra, int Unreadable, IReadOnlyList<JudgeFieldScore> Fields)
{
    /// <summary>Tỷ lệ lượt mà toàn bộ phán quyết khớp đáp án chuẩn.</summary>
    public double? ExactRate => Cases == 0 ? null : (double)ExactCases / Cases;

    /// <summary>Tỷ lệ lượt có ít nhất một khẳng định thừa — con số quyết định có dùng tiếp model hay không.</summary>
    public double? ExtraRate => Cases == 0 ? null : (double)CasesWithExtra / Cases;
}
