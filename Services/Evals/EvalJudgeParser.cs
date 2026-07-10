using System.Text.Json;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Evals;

/// <summary>
/// Parse phản hồi của judge — kỳ vọng JSON <c>{"score": 1-5, "reasoning": "..."}</c> (prompt
/// Eval/judge.v1.md), khoan dung với code-fence/văn dẫn quanh JSON (JsonExtractor). Điểm ngoài 1–5 bị
/// kẹp về biên thay vì loại bỏ (model đôi khi trả 0/6 cho ý "tệ nhất/tốt nhất").
/// </summary>
public static class EvalJudgeParser
{
    public static bool TryParse(string? responseText, out int score, out string reasoning)
    {
        score = 0;
        reasoning = string.Empty;

        if (string.IsNullOrWhiteSpace(responseText))
            return false;

        var json = JsonExtractor.Extract(responseText);
        if (string.IsNullOrEmpty(json))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<JudgeVerdict>(json, JsonDefaults.CaseInsensitive);
            if (parsed?.Score is not int raw)
                return false;

            score = Math.Clamp(raw, 1, 5);
            reasoning = parsed.Reasoning?.Trim() ?? string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class JudgeVerdict
    {
        public int? Score { get; set; }
        public string? Reasoning { get; set; }
    }
}
