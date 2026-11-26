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
/// Lượt RÁP BẢNG BÁO CÁO: khi nhóm «Báo cáo / thống kê» đã <c>[RÕ]</c> (<see cref="ReportMapGate"/>), đọc
/// hội thoại, ráp các báo cáo người dùng đã chọn / đã kể thành <see cref="ReportMapRow"/>[], lưu vào
/// <see cref="Project.ReportMap"/> rồi gieo chúng vào bảng màn hình ở trạng thái CHƯA CHỐT: một màn tổng
/// quan gom các báo cáo (mỗi báo cáo một chức năng), cộng màn riêng của báo cáo nào cần — xem
/// <see cref="ReportMapBuilder"/>.
///
/// <para>
/// <b>Vì sao bảng này KHÔNG được bày ra cho người dùng chốt.</b> Nhóm báo cáo lên <c>[RÕ]</c> bằng chính
/// lựa chọn của người dùng: BA đề xuất 2–4 báo cáo dưới dạng chip *"tên — để mục đích"*, họ tích cái cần
/// và gõ thêm cái thiếu. Bày tiếp một bảng để "chốt lại" là hỏi lần thứ hai đúng điều vừa được trả lời. Hai
/// cột mà bảng có thêm ("lấy số từ", "gộp / lọc theo") là thứ BA ráp từ lời họ, không phải thứ họ sửa. Chỗ
/// người dùng góp ý về báo cáo là bản demo (POC Review), nơi họ thấy màn hình báo cáo thật.
/// </para>
///
/// <para>
/// <b>Mỗi báo cáo thành một TÁC VỤ của bản thiết kế UX</b> (<see cref="UxInventoryBuilder.ReportTasks"/>, tất
/// định — không để model kể lại), kèm chỗ đứng bảng này đã chốt (khối của màn tổng quan, ô đếm, hay màn
/// riêng). Không có đường đó thì một báo cáo nằm lại trong cột <c>ReportMap</c> không có dòng nào ở bảng phân
/// quyền và không có chỗ nào ở <c>## 6. Screens To Generate</c> — mặc nhiên "không ai được xem" một thứ
/// người dùng vừa đặt hàng.
/// </para>
///
/// <para>
/// <b>Chạy ở ĐẦU lượt chat</b> (<c>BAChatService</c>) ngay khi nhóm «Báo cáo / thống kê» vừa <c>[RÕ]</c>, và chạy
/// bù một lần nữa ở bước "Write Requirement" (<see cref="DesignPreparationService"/>) ngay trước lượt thiết kế UX
/// — lượt thiết kế gieo mỗi báo cáo thành một tác vụ, nên bảng phải có mặt trước nó. <b>Fail-open:</b> lời gọi
/// lỗi, hoặc không dòng nào dùng được, thì không ghi gì — cổng còn mở nên lượt kế ráp lại.
/// </para>
/// </summary>
public class ReportMapDraftService
{
    public const string PromptKey = "BusinessAnalyst/report-map-draft.v1.md";

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public ReportMapDraftService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>
    /// Ráp và lưu bảng báo cáo nếu đã tới lúc, rồi trả về số báo cáo vừa lưu (0 khi không chạy hoặc không
    /// ráp được). <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK.
    /// </summary>
    public async Task<int> UpdateAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        if (!ReportMapGate.ShouldDraft(project))
            return 0;

        var turns = await _db.AgentConversations
            .AsNoTracking()
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

        var draft = await DraftAsync(project, turns, ba, model, cancellationToken);
        var rows = ReportMapBuilder.Build(draft?.Reports, draft?.Dashboard);
        if (rows.Count == 0)
            return 0;

        project.ReportMap = JsonSerializer.Serialize(rows);

        await _db.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    private async Task<ReportMapDraft?> DraftAsync(Project project, List<AgentConversation> turns, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        // Phần bản đồ đã ghi nhận về nhóm: với buổi phỏng vấn dài, đây là bản cô đọng những gì người dùng đã
        // chọn — model đối chiếu nó với hội thoại thay vì tự lọc lại từ đầu thứ nào là báo cáo.
        var known = CoverageMapParser.Parse(project.RequirementCoverageMap)
            .FirstOrDefault(x => x.Key == CoverageGroupKey.Reports)?.Known ?? Array.Empty<string>();
        sb.AppendLine("## Điều đã ghi nhận về nhóm «Báo cáo / thống kê»");
        sb.AppendLine(known.Count > 0 ? string.Join("\n", known.Select(k => "- " + k)) : "(chưa ghi gì)");
        sb.AppendLine();
        sb.AppendLine("## Hội thoại");
        // Lượt ⚠️ (lời gọi LLM lỗi) và cặp hỏi–đáp ngoài phạm vi không chở nhu cầu nào — xem
        // ConversationTurnRenderer.CarriesNoRequirement.
        foreach (var t in turns.Where(t => !ConversationTurnRenderer.CarriesNoRequirement(t)))
            sb.AppendLine($"- {ConversationTurnRenderer.Render(t)}");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get(PromptKey)),
            new(ChatRole.User, sb.ToString())
        };

        var (callResult, structured) = await _llm.ChatStructuredAsync<ReportMapDraft>(
            model, messages, new ModelCallLogContext(project.Id, ba, "BAReportMapDraft"),
            cancellationToken: cancellationToken);

        return callResult.IsSuccess ? structured : null;
    }
}
