using System.Text.Json;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Evals;

/// <summary>Một dòng tiêu chí đã được judge đối chiếu: đạt hay trượt, trượt ở chỗ nào.</summary>
public record EvalCriterionVerdict(string Criterion, bool Passed, string? Note);

/// <summary>
/// Phán quyết của judge cho MỘT scenario: điểm tổng, lý do, và (tuỳ model) bảng đối chiếu từng tiêu chí.
/// </summary>
public record EvalJudgeVerdict(int Score, string Reasoning, IReadOnlyList<EvalCriterionVerdict> Criteria);

/// <summary>
/// Parse phản hồi của judge — kỳ vọng JSON <c>{"score": 1-5, "reasoning": "...", "criteria": [...]}</c>
/// (prompt Eval/judge.v1.md), khoan dung với code-fence/văn dẫn quanh JSON (<see cref="LlmJson"/>). Điểm
/// ngoài 1–5 bị kẹp về biên thay vì loại bỏ (model đôi khi trả 0/6 cho ý "tệ nhất/tốt nhất").
/// <para>
/// <c>criteria</c> là phần MỞ RỘNG, không bắt buộc: kết quả cũ trong DB và model không chịu trả mảng này
/// vẫn parse được với danh sách rỗng — mất bảng đối chiếu chứ không mất điểm. Phần tử rác (thiếu tên tiêu
/// chí) bị bỏ qua từng cái, vì một dòng hỏng không đáng làm hỏng cả phán quyết.
/// </para>
/// </summary>
public static class EvalJudgeParser
{
    /// <summary>Cắt tiêu chí quá dài (judge được yêu cầu rút gọn ~120 ký tự, nhưng không phải lúc nào cũng nghe).</summary>
    private const int MaxCriterionLength = 300;

    public static bool TryParse(string? responseText, out EvalJudgeVerdict verdict)
    {
        verdict = default!;

        var parsed = LlmJson.TryDeserialize<JudgeResponse>(responseText);
        if (parsed?.Score is not int raw)
            return false;

        verdict = new EvalJudgeVerdict(
            Math.Clamp(raw, 1, 5),
            parsed.Reasoning?.Trim() ?? string.Empty,
            ReadCriteria(parsed.Criteria));
        return true;
    }

    /// <summary>Đọc lại bảng tiêu chí đã lưu trên <c>EvalResult.CriteriaJson</c>; rỗng khi không có/không đọc được.</summary>
    public static IReadOnlyList<EvalCriterionVerdict> ReadCriteriaJson(string? criteriaJson)
    {
        if (string.IsNullOrWhiteSpace(criteriaJson))
            return Array.Empty<EvalCriterionVerdict>();

        try
        {
            return ReadCriteria(JsonSerializer.Deserialize<List<JudgeCriterion>>(criteriaJson, LlmJson.Options));
        }
        catch (JsonException)
        {
            return Array.Empty<EvalCriterionVerdict>();
        }
    }

    /// <summary>Serialize để lưu lên <c>EvalResult.CriteriaJson</c>; null khi judge không trả tiêu chí nào.</summary>
    public static string? ToJson(IReadOnlyList<EvalCriterionVerdict> criteria) =>
        criteria.Count == 0 ? null : JsonSerializer.Serialize(criteria);

    private static IReadOnlyList<EvalCriterionVerdict> ReadCriteria(List<JudgeCriterion>? raw)
    {
        if (raw == null || raw.Count == 0)
            return Array.Empty<EvalCriterionVerdict>();

        var items = new List<EvalCriterionVerdict>(raw.Count);
        foreach (var item in raw)
        {
            var criterion = item?.Criterion?.Trim();
            if (string.IsNullOrEmpty(criterion))
                continue;

            if (criterion.Length > MaxCriterionLength)
                criterion = criterion[..MaxCriterionLength] + "…";

            var note = item!.Note?.Trim();
            items.Add(new EvalCriterionVerdict(criterion, item.Passed ?? false, string.IsNullOrEmpty(note) ? null : note));
        }

        return items;
    }

    private sealed class JudgeResponse
    {
        public int? Score { get; set; }
        public string? Reasoning { get; set; }
        public List<JudgeCriterion>? Criteria { get; set; }
    }

    private sealed class JudgeCriterion
    {
        public string? Criterion { get; set; }
        // Nullable: judge bỏ trống trường này thì coi như TRƯỢT — an toàn hơn là mặc định "đạt".
        public bool? Passed { get; set; }
        public string? Note { get; set; }
    }
}
