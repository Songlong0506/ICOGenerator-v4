using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Lượt XẾP CHỖ: các bước luồng mà <see cref="ScreenScopeMapBuilder.UncoveredActions"/> vừa gọi tên được
/// BA gán về đúng chức năng của đúng màn hình, TRƯỚC khi bảng màn hình hiện ra.
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
/// vào tài liệu — nó thành dòng TÍCH SẴN trên chính bảng người dùng đang rà.
/// </para>
///
/// <para>
/// <b>FAIL-OPEN toàn phần</b>, như mọi bộ nhớ và mọi cổng phụ khác: lời gọi lỗi, model trả rác, hay không
/// xếp được mục nào ⇒ bảng giữ nguyên và dòng nhắc cũ hiện ra như trước. Lượt này chỉ được phép LÀM TỐT
/// HƠN một bảng, không bao giờ được phép chặn nó hiện ra.
/// </para>
/// </summary>
public class ScreenStepPlacementService
{
    /// <summary>
    /// Trần số bước mồ côi đưa đi xếp chỗ trong MỘT lượt. Nhiều hơn thế thì thứ hỏng không phải vài chỗ
    /// trống mà là cả bảng (model bỏ trắng ô "phục vụ bước"), và vá từng bước một lúc đó chỉ đắp thêm chức
    /// năng bịa lên một bảng vốn đã sai — ca đó phải để dòng nhắc nói thật với người dùng.
    /// </summary>
    public const int MaxSteps = 8;

    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public ScreenStepPlacementService(ILlmClient llm, PromptTemplateService prompts)
    {
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>
    /// Trả về bảng đã được lấp các bước mồ côi. Không xếp được gì (hoặc không có gì để xếp) ⇒ trả lại đúng
    /// <paramref name="rows"/>.
    /// </summary>
    public async Task<List<ScreenScopeRow>> PlaceAsync(
        Guid projectId,
        IReadOnlyList<ScreenScopeRow> rows,
        IReadOnlyList<string> uncoveredSteps,
        Agent ba,
        AiModel model,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0 || uncoveredSteps.Count == 0 || uncoveredSteps.Count > MaxSteps)
            return rows.ToList();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/screen-step-placement.v1.md")),
            new(ChatRole.User, RenderInput(rows, uncoveredSteps))
        };

        var (callResult, plan) = await _llm.ChatStructuredAsync<ScreenStepPlacementPlan>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAScreenStepPlacement"),
            cancellationToken: cancellationToken);

        if (!callResult.IsSuccess || plan == null)
            return rows.ToList();

        return ScreenScopeMapBuilder.ApplyPlacements(rows, plan.Placements, uncoveredSteps);
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
    private static string RenderInput(IReadOnlyList<ScreenScopeRow> rows, IReadOnlyList<string> uncoveredSteps)
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

        sb.AppendLine();
        sb.AppendLine("## Các bước MỒ CÔI cần xếp chỗ (chép đúng chữ vào `step`)");
        foreach (var step in uncoveredSteps)
            sb.AppendLine("- " + step);

        return sb.ToString();
    }
}
