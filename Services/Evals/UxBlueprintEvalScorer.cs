using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Services.Evals;

/// <summary>
/// Chấm TẤT ĐỊNH đầu ra của prompt thiết kế UX (<c>UiUx/ux-blueprint.v1.md</c>) trong Prompt Evals — cùng
/// builder, cùng lint, cùng bộ chỉ số với production (<see cref="UxBlueprintBuilder"/>, <see cref="UxBlueprintLint"/>,
/// <see cref="UxJourneyAnalyzer"/>).
///
/// <para>
/// <b>Vì sao không để judge tự chấm.</b> "Bản thiết kế này tốt hơn" là câu một model chấm được nhưng không kiểm
/// được, và điểm 1–5 của judge dao động theo cách nó đọc. Số màn trên mỗi đối tượng, số lần đổi màn mỗi luồng,
/// số lỗi lint thì không dao động — chúng là baseline để so trước/sau mỗi lần đổi prompt hay model. Judge vẫn
/// chấm phần ngữ nghĩa (tên màn có đọc như nơi chốn không, lý do thiết kế có thuyết phục không), nhưng nó đọc
/// CÙNG bộ chỉ số này ngay trong đầu vào của nó.
/// </para>
///
/// <para>
/// Đo bản MODEL trả, trước vòng vá tất định (<see cref="UxBlueprintBuilder.Repair"/>): vá xong thì mọi bản đều
/// "đủ", và chỉ số mất khả năng phân biệt một prompt tốt với một prompt dựa vào lưới an toàn.
/// </para>
/// </summary>
public static class UxBlueprintEvalScorer
{
    public static bool Applies(string? promptKey)
        => string.Equals(promptKey, UxDesignService.BlueprintPromptKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Kết quả chấm: bản thiết kế đã chuẩn hoá + chỉ số + phát hiện; null khi đầu vào/đầu ra không đọc được.</summary>
    public sealed record Result(UxBlueprint Blueprint, UxMetrics Metrics, IReadOnlyList<UxFinding> Findings);

    public static Result? Score(string? userInput, string? output)
    {
        var input = UxDesignInputBuilder.TryParse(userInput);
        if (input == null)
            return null;

        var draft = LlmJson.TryDeserialize<UxBlueprintDraft>(output, requireKnownProperty: true);
        if (draft == null)
            return null;

        var (inventory, flowMapJson) = UxDesignInputBuilder.ToInventory(input);
        var blueprint = UxBlueprintBuilder.FromDraft(draft, inventory, input.Roles);
        blueprint.Journeys = UxJourneyAnalyzer.Journeys(blueprint, flowMapJson);
        blueprint.Findings = UxBlueprintLint.Check(blueprint, flowMapJson);
        blueprint.Metrics = UxJourneyAnalyzer.Metrics(blueprint, flowMapJson);
        return new Result(blueprint, blueprint.Metrics, blueprint.Findings);
    }

    /// <summary>Khối văn bản nối vào đầu vào của judge và vào lời chấm lưu lại.</summary>
    public static string Render(Result? result)
    {
        if (result == null)
            return "## Chỉ số tất định\n(không đọc được đầu ra thành một bản thiết kế — coi như hỏng định dạng)";

        var sb = new StringBuilder();
        sb.AppendLine("## Chỉ số tất định (code tự đo trên đầu ra, trước mọi bước vá)");
        sb.AppendLine(UxJourneyAnalyzer.Summary(result.Metrics));
        foreach (var finding in result.Findings.Take(15))
            sb.AppendLine($"- [{finding.Severity}] {finding.Code}: {finding.Message}");
        return sb.ToString().TrimEnd();
    }
}
