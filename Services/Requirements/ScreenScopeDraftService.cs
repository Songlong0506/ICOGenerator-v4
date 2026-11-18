using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Lượt SOẠN PHẠM VI MÀN HÌNH: khi <see cref="ScreenScopeGate"/> mở, BA tự soạn việc của từng màn, các
/// chức năng trên màn và bước luồng mà từng chức năng phụ trách, xếp chỗ nốt các bước luồng còn mồ côi
/// (<see cref="ScreenStepPlacementService"/>), rồi CHỐT cả bảng vào <see cref="Project.ScreenScopeMap"/>.
///
/// <para>
/// <b>Vì sao người dùng không rà bảng này.</b> Việc của người dùng là cung cấp yêu cầu; cần bao nhiêu màn
/// hình, chức năng nào đặt trên màn nào là thiết kế — phần họ đi thuê BA để làm. Bảng màn hình từng được bày
/// ra cho họ chốt, và mỗi lần phạm vi trôi sau lúc chốt thì BA lại hỏi ngược họ kiểu *"nút Thu hồi tủ và
/// nút Đã clean nên đặt ở màn hình nào?"* (ca thật: dự án Locker, khi người dùng thêm trạng thái Need
/// Clean). Nay BA tự quyết, và chỗ người dùng góp ý về màn hình là POC Review, nơi họ thấy màn hình thật.
/// </para>
///
/// <para>
/// <b>Chạy ở ĐẦU lượt chat</b> (<c>BAChatService</c>), sau lượt ráp bảng báo cáo và lượt chắt lọc phạm vi
/// (hai nguồn màn hình), trước lượt soạn bảng phân quyền — đúng thứ tự phụ thuộc. <b>Fail-open:</b> lời gọi
/// lỗi thì không ghi gì, cổng còn mở nên lượt kế soạn lại; lời gọi thành công mà không dùng được dòng nào
/// thì vẫn chốt phạm vi đang có (dòng nào cũng đã có tên màn hình) — một model không trả nổi structured
/// output không được phép treo bảng phân quyền và nút "Write Requirement" mãi mãi.
/// </para>
/// </summary>
public class ScreenScopeDraftService
{
    public const string PromptKey = "BusinessAnalyst/screen-scope-draft.v1.md";

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly ScreenStepPlacementService _placement;

    public ScreenScopeDraftService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts, ScreenStepPlacementService placement)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _placement = placement;
    }

    /// <summary>
    /// Soạn và chốt phạm vi màn hình nếu đã tới lúc. Trả true khi đã ghi bảng.
    /// <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK.
    /// </summary>
    public async Task<bool> UpdateAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        if (!ScreenScopeGate.ShouldDraft(project))
            return false;

        var screens = ScreenScopeMapBuilder.EffectiveScreens(project.ScreenScopeMap);
        if (screens.Count == 0)
            return false;

        var turns = await _db.AgentConversations
            .AsNoTracking()
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get(PromptKey)),
            new(ChatRole.User, RenderInput(project, screens, turns))
        };

        var (callResult, draft) = await _llm.ChatStructuredAsync<ScreenScopeDraft>(
            model, messages, new ModelCallLogContext(project.Id, ba, "BAScreenScopeDraft"),
            cancellationToken: cancellationToken);
        if (!callResult.IsSuccess)
            return false;

        // GIEO bằng chính bảng đang lưu: chỉ dòng đang lưu được chở dấu đã-chốt qua, và chỉ phần CHƯA CHỐT
        // của nó mới cho model lấp vào (xem ScreenScopeMapBuilder.Build). Ở lần soạn LẠI, đây là thứ giữ
        // nguyên mọi màn hình đã chốt trong khi lượt này chỉ lo phần vừa lộ ra.
        var rows = ScreenScopeMapBuilder.Build(
            ScreenScopeMapBuilder.SeedRows(project.ScreenScopeMap), draft?.Screens, screens);
        if (rows.Count == 0)
            return false;

        // MỐI NỐI luồng ⇄ màn hình: bước luồng đã chốt mà không chức năng nào nhận thì BA tự xếp chỗ (hoặc
        // quyết là làm ngoài phần mềm) — xem ScreenStepPlacementService.
        var uncovered = ScreenScopeMapBuilder.UncoveredActions(rows, project.FlowMap);
        if (uncovered.Count > 0)
        {
            var placement = await _placement.PlaceAsync(
                project.Id, rows, uncovered, project.FlowMap, ba, model, cancellationToken);
            rows = placement.Rows;

            // Quyết định "làm ngoài phần mềm" được GHI lên bảng luồng: phép kiểm thôi đòi chức năng cho bước
            // đó ở mọi lần soạn sau, và khối "bảng luồng đã chốt" chở nhãn ấy tới tận AI Design Spec.
            var marked = FlowMapBuilder.MarkOutsideSoftware(project.FlowMap, placement.Outside);
            if (marked != null)
                project.FlowMap = marked;
        }

        // CHỐT: mọi dòng và mọi chức năng của bảng vừa soạn mang dấu đã-chốt, nên cổng đóng ngay và chỉ mở
        // lại khi lượt chắt lọc ghép thêm phần MỚI. Settle giữ các BIA của bảng đang lưu (phần đã loại của
        // dự án cũ) để lượt chắt lọc không dựng lại thứ đã loại.
        project.ScreenScopeMap = JsonSerializer.Serialize(ScreenScopeMapBuilder.Settle(project.ScreenScopeMap, rows));

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Đầu vào của lời gọi: phạm vi cho phép, phần MỚI (ở lần soạn lại), các bước luồng đã chốt, bảng báo
    /// cáo, rồi hội thoại. Phần DỮ LIỆU này phải khớp đúng với builder đọc lại kết quả — tên màn hình model
    /// chép phải là tên trong danh sách cho phép, và bước luồng phải là phần <c>action</c> của bảng luồng.
    /// </summary>
    private static string RenderInput(Project project, IReadOnlyList<string> screens, IReadOnlyList<AgentConversation> turns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Phạm vi màn hình (mỗi mục phải hoặc thành MỘT dòng `screen`, hoặc nằm trong `covers` của một dòng — không mục nào được bỏ rơi)");
        sb.AppendLine(string.Join("\n", screens.Select(s => "- " + s)));

        // Lần soạn LẠI: phần đã chốt giữ nguyên (SeedRows), việc của lượt này chỉ là phần vừa lộ ra. Không
        // gọi tên nó thì model mô tả lại cả bảng — công bỏ đi, vì Build sẽ giữ nguyên các dòng đã chốt.
        if (ScreenScopeMapBuilder.IsConfirmed(project.ScreenScopeMap))
        {
            var pendingScreens = ScreenScopeMapBuilder.PendingScreens(project.ScreenScopeMap);
            if (pendingScreens.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Màn hình MỚI (lộ ra SAU lần chốt trước — phần việc DUY NHẤT của lượt này)");
                sb.AppendLine(string.Join("\n", pendingScreens.Select(s => "- " + s)));
                sb.AppendLine("Mục nào trong số này thật ra chỉ là một chức năng của màn hình ĐÃ CHỐT thì đừng dựng thành dòng riêng: ghi nguyên văn nó vào `covers` của dòng ấy.");
            }

            var pendingFunctions = ScreenScopeMapBuilder.PendingFunctions(project.ScreenScopeMap);
            if (pendingFunctions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Chức năng MỚI trên màn hình đã chốt (đã có sẵn trong bảng, đừng dựng dòng mới — chỉ gắn bước luồng)");
                sb.AppendLine(string.Join("\n", pendingFunctions.Select(s => "- " + s)));
            }
        }

        var actions = FlowMapBuilder.IncludedActions(project.FlowMap);
        if (actions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Các BƯỚC của bảng luồng đã chốt (mỗi bước phải có ÍT NHẤT MỘT chức năng nhận vào `flowSteps`)");
            sb.AppendLine(string.Join("\n", actions.Select(a => "- " + a)));
        }

        var reports = ReportMapBuilder.RenderConfirmedBlock(project.ReportMap);
        if (!string.IsNullOrWhiteSpace(reports))
        {
            sb.AppendLine();
            sb.AppendLine("## Bảng báo cáo / thống kê của ứng dụng");
            sb.AppendLine(reports.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("## Hội thoại");
        // Lượt ⚠️ (lời gọi LLM lỗi) không chở yêu cầu nào — xem ConversationTurnRenderer.IsFailureTurn.
        foreach (var t in turns.Where(t => !ConversationTurnRenderer.IsFailureTurn(t)))
            sb.AppendLine($"- {ConversationTurnRenderer.Render(t)}");

        return sb.ToString();
    }
}
