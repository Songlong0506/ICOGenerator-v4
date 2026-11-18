using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Lượt XẾP CHỖ: các bước luồng mà <see cref="ScreenScopeMapBuilder.UncoveredActions"/> vừa gọi tên được
/// BA gán về đúng chức năng của đúng màn hình, ngay trong lượt soạn phạm vi (<see cref="ScreenScopeDraftService"/>)
/// và TRƯỚC khi phạm vi được chốt.
///
/// <para>
/// <b>Việc này thay cho một câu hỏi ngược người dùng.</b> Trước đây bảng hiện ra kèm dòng nhắc *"Chưa chức
/// năng nào phụ trách các bước: … Anh/chị điền bước đó vào ô bên phải của chức năng phù hợp, hoặc nhắn cho
/// mình biết nếu thiếu hẳn một màn hình"*. Câu đó đòi người dùng nghiệp vụ làm hai việc của BA — ánh xạ
/// một bước nghiệp vụ sang một chức năng trên một màn hình, và nhận ra khi cả phạm vi màn hình còn thiếu
/// một chỗ — ngay sau khi họ vừa rà một bảng mười mấy dòng. Ca thật (JD Library 2): bước mồ côi là *"Xem
/// danh sách nhân viên trực tiếp dưới quyền"*, bước 4 của luồng chính chính họ vừa chốt, và chỗ đúng của
/// nó là một chức năng trên màn <c>JD Assignment</c> đang nằm ngay trên bảng.
/// </para>
///
/// <para>
/// <b>Phân vai giữa máy và model giữ nguyên như cũ.</b> Code vẫn là nơi quyết định có lỗ hổng hay không
/// (<c>UncoveredActions</c>) và lời xếp chỗ nào được nhận (<see cref="ScreenScopeMapBuilder.ApplyPlacements"/>
/// chỉ nhận mục trỏ đúng vào bước mồ côi, chỉ THÊM, không bao giờ bớt). Model chỉ trả lời đúng một câu mà
/// không phép so chuỗi nào làm thay được: bước này là việc của chức năng nào. Và kết quả không đi thẳng
/// vào tài liệu mà qua đúng chốt chặn đó vào phạm vi màn hình.
/// </para>
///
/// <para>
/// <b>Bước không thuộc ứng dụng thì BA cũng TỰ QUYẾT.</b> Một bước con người làm ngoài phần mềm (gọi điện,
/// xuống kiểm tra tận nơi, bộ phận khác gửi file qua email) không có chức năng nào để gắn — model khai nó ở
/// <see cref="ScreenStepPlacementPlan.OutsideSoftware"/> kèm một câu lý do, và quyết định ấy được ghi lên
/// bảng luồng (<see cref="FlowMapStep.OutsideSoftware"/>). Trước đây đúng loại bước này bị trả về người
/// dùng bằng dòng nhắc *"mình chưa xếp được các bước này… anh/chị nhắn cho mình biết là bên nào"*: lại
/// một quyết định thiết kế hỏi ngược người đi thuê BA. Dòng nhắc đó đã gỡ hẳn.
/// </para>
///
/// <para>
/// <b>FAIL-OPEN toàn phần</b>, như mọi bộ nhớ và mọi cổng phụ khác: lời gọi lỗi hay model trả rác ⇒ bảng
/// giữ nguyên và vẫn được chốt. Lượt này chỉ được phép LÀM TỐT HƠN một bảng, không bao giờ được phép chặn
/// nó; bước còn sót được xét lại ở lần soạn phạm vi kế tiếp.
/// </para>
/// </summary>
public class ScreenStepPlacementService
{
    /// <summary>
    /// Trần số bước mồ côi đưa đi xếp chỗ trong MỘT lượt — đủ cho trọn một bảng luồng thường gặp (ba luồng,
    /// mỗi luồng tối đa <see cref="FlowMapBuilder.MaxStepsPerFlow"/> bước). Nó chỉ giữ cho output của lời
    /// gọi có đáy; vượt trần thì xếp phần ĐẦU danh sách (bước của luồng chính đứng trước), phần dư chờ
    /// lần soạn phạm vi kế tiếp.
    ///
    /// <para>
    /// Trần này từng là 8 và vượt trần là BỎ CẢ LƯỢT, với lý lẽ "nhiều bước mồ côi thế thì cả bảng hỏng,
    /// vá từng bước chỉ đắp chức năng bịa". Ca thật (dự án Locker) cho thấy vế sau sai: bảng chỉ có bốn màn
    /// hình báo cáo, cả 13 bước của luồng đều mồ côi, lượt xếp chỗ đứng ngoài — và toàn bộ việc thiết kế
    /// màn hình rơi xuống dòng nhắc cho người dùng tự làm. Bảng càng hỏng thì càng là lúc BA phải dựng nốt.
    /// </para>
    /// </summary>
    public const int MaxSteps = 30;

    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public ScreenStepPlacementService(ILlmClient llm, PromptTemplateService prompts)
    {
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>
    /// Số lượt gọi tối đa cho MỘT lần xếp chỗ. Lượt hai chỉ chạy khi lượt đầu còn để sót bước — không xếp
    /// chỗ, cũng không khai là làm ngoài phần mềm — và chỉ đưa đúng các bước sót ấy đi hỏi lại.
    /// </summary>
    public const int MaxAttempts = 2;

    /// <summary>Kết quả lượt xếp chỗ: bảng đã lấp, và các bước BA quyết định là làm ngoài phần mềm.</summary>
    public sealed record Result(List<ScreenScopeRow> Rows, List<ScreenStepOutside> Outside);

    /// <summary>
    /// Quyết định số phận của TỪNG bước mồ côi: có chỗ trên bảng màn hình, hoặc làm ngoài phần mềm. Không
    /// quyết được gì (lời gọi lỗi) ⇒ trả lại đúng <paramref name="rows"/> và danh sách ngoài phần mềm rỗng.
    /// </summary>
    public async Task<Result> PlaceAsync(
        Guid projectId,
        IReadOnlyList<ScreenScopeRow> rows,
        IReadOnlyList<string> uncoveredSteps,
        string? flowMapJson,
        Agent ba,
        AiModel model,
        CancellationToken cancellationToken = default)
    {
        var current = rows.ToList();
        var outside = new List<ScreenStepOutside>();
        if (current.Count == 0 || uncoveredSteps.Count == 0)
            return new Result(current, outside);

        IReadOnlyList<string> pending = uncoveredSteps.Count > MaxSteps
            ? uncoveredSteps.Take(MaxSteps).ToList()
            : uncoveredSteps;

        for (var attempt = 0; attempt < MaxAttempts && pending.Count > 0; attempt++)
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _prompts.Get("BusinessAnalyst/screen-step-placement.v1.md")),
                new(ChatRole.User, RenderInput(current, pending, flowMapJson))
            };

            var (callResult, plan) = await _llm.ChatStructuredAsync<ScreenStepPlacementPlan>(
                model, messages, new ModelCallLogContext(projectId, ba, "BAScreenStepPlacement"),
                cancellationToken: cancellationToken);

            if (!callResult.IsSuccess || plan == null)
                break;

            // Xếp chỗ THẮNG lời khai "ngoài phần mềm" khi model nói cả hai về cùng một bước: một chức năng
            // thừa hiện ra ngay trên bản demo để người dùng chê ở POC Review, còn một bước bị gạt khỏi ứng
            // dụng thì họ khó thấy hơn nhiều.
            current = ScreenScopeMapBuilder.ApplyPlacements(current, plan.Placements, pending);
            var open = pending.Except(ScreenScopeMapBuilder.CoveredActions(current, pending)).ToList();
            var declared = ScreenScopeMapBuilder.ResolveOutsideSteps(plan.OutsideSoftware, open);
            outside.AddRange(declared);
            pending = open.Except(declared.Select(d => d.Step), StringComparer.OrdinalIgnoreCase).ToList();
        }

        return new Result(current, outside);
    }

    /// <summary>
    /// Bảng hiện tại + danh sách bước mồ côi, đúng hình dạng mà prompt mô tả.
    ///
    /// <para>
    /// Bảng phải chở CẢ tên chức năng lẫn ô "phục vụ bước" đang có: không có chúng thì model không phân
    /// biệt được ca "đã có chức năng đúng việc, chỉ thiếu ô bước" với ca "phải thêm chức năng mới", mà đó
    /// là hai nhánh đầu tiên của prompt. Chỉ liệt kê phần CÒN TÍCH — bỏ tích một chức năng là bỏ luôn phần
    /// việc nó gánh, nên gợi ý model gắn bước vào đó là gắn vào một dòng đã tắt.
    /// </para>
    /// </summary>
    private static string RenderInput(IReadOnlyList<ScreenScopeRow> rows, IReadOnlyList<string> uncoveredSteps, string? flowMapJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Bảng màn hình hiện tại (các màn hình và chức năng đang có)");
        foreach (var row in rows.Where(r => r.Included))
        {
            sb.Append("- ").Append(row.Screen);
            if (!string.IsNullOrWhiteSpace(row.Purpose))
                sb.Append(" — ").Append(row.Purpose.Trim());
            sb.AppendLine();

            var functions = row.Functions.Where(f => f.Included).ToList();
            if (functions.Count == 0)
            {
                sb.AppendLine("  · (chưa có chức năng nào)");
                continue;
            }

            foreach (var function in functions)
            {
                sb.Append("  · ").Append(function.Name);
                if (function.FlowSteps.Count > 0)
                    sb.Append(" [đang phụ trách: ").Append(string.Join("; ", function.FlowSteps)).Append(']');
                sb.AppendLine();
            }
        }

        // AI LÀM và SAU BƯỚC ĐÓ RA SAO đi kèm từng bước: "ngoài phần mềm hay trong ứng dụng" là câu hỏi về
        // người làm và về thứ ứng dụng phải ghi nhận, mà một dòng hành động trần thì không trả lời được.
        var details = FlowMapBuilder.Parse(flowMapJson)
            .SelectMany(flow => flow.Steps.Where(s => s.Included).Select(s => (Flow: flow.Name, Step: s)))
            .ToList();

        sb.AppendLine();
        sb.AppendLine("## Các bước MỒ CÔI cần quyết định (chép đúng chữ sau dấu «bước:» vào `step`)");
        foreach (var step in uncoveredSteps)
        {
            sb.Append("- bước: ").AppendLine(step);
            var detail = details.FirstOrDefault(d => string.Equals(d.Step.Action.Trim(), step.Trim(), StringComparison.OrdinalIgnoreCase));
            if (detail.Step == null)
                continue;

            sb.Append("  (luồng: ").Append(detail.Flow);
            if (!string.IsNullOrWhiteSpace(detail.Step.Actor))
                sb.Append(" · người làm: ").Append(detail.Step.Actor.Trim());
            if (!string.IsNullOrWhiteSpace(detail.Step.Outcome))
                sb.Append(" · sau bước này: ").Append(detail.Step.Outcome.Trim());
            sb.AppendLine(")");
        }

        return sb.ToString();
    }
}
