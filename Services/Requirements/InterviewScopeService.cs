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
/// Lượt chắt lọc PHẠM VI MÀN HÌNH: đọc hội thoại, tìm các màn hình / chức năng chưa có trong bảng màn hình
/// và ghép chúng vào bảng ấy ở trạng thái CHỜ DUYỆT (<see cref="ScreenScopeMapBuilder.Merge"/>), để
/// <see cref="ScreenScopeGate"/> bày ra hỏi.
///
/// <para>
/// <b>Nó chạy THƯA, và đó là điểm khác duy nhất đáng kể so với các tầng chắt lọc khác.</b> Danh sách này
/// từng là mục thứ ba của <see cref="InterviewOutlookService"/> nên đi theo nhịp của lời gọi đó — sau MỖI
/// lượt chat, từ lượt đầu tiên. Nhịp ấy đúng cho hai danh sách kia (tồn đọng câu hỏi phải tươi để nạp vào
/// ngữ cảnh lượt sau) nhưng sai cho phạm vi màn hình, thứ chỉ được tiêu thụ khi bảng được bày ra hỏi — một
/// hai lần trong cả buổi. Cái giá của nhịp cũ có hai phần:
/// </para>
/// <list type="bullet">
///   <item><b>Token.</b> Luật đặt tên màn hình cộng luật "chỉ màn hình, chức năng thì gộp vào màn chứa nó"
///   chiếm hơn một phần ba prompt chắt lọc, và khối "bảng màn hình đang có" phải kể tới từng chức năng để
///   model biết cái gì đã có. Cả hai đi theo mọi lượt chat của buổi phỏng vấn để phục vụ một hai lượt.</item>
///   <item><b>Chất lượng, và phần này đắt hơn.</b> Ở đầu buổi thì bảng luồng chưa chốt, bảng đối tượng chưa
///   có, phạm vi chưa hình thành — mọi màn hình model đoán ra lúc ấy là phỏng đoán sớm. Mà
///   <see cref="ScreenScopeMapBuilder.Merge"/> chỉ được phép THÊM: không dòng nào bị xoá, không cờ tích nào
///   bị đổi. Một dòng sai sinh ra ở lượt 3 nằm lại trong bảng cho tới khi chính người dùng bỏ tích nó ở
///   lượt 25 — nếu họ nhận ra.</item>
/// </list>
///
/// <para>
/// <b>Nhịp mới</b> (<see cref="ShouldHarvest"/>): im lặng cho tới khi bản đồ bao phủ đi tới sát cổng bảng
/// màn hình <b>VÀ ba bảng đứng trước (luồng, đối tượng, báo cáo) đã hết việc</b>, rồi gộp bù TRỌN quãng đã
/// qua trong một lời gọi. Sau lần chốt đầu, phần phạm vi trôi tiếp được gộp theo LÔ
/// (<see cref="HarvestBatchThreshold"/>) — cùng khuôn với
/// <see cref="UserMemoryService.HarvestBatchThreshold"/>, và cùng lý do: một lượt chắt lọc chạy sau lưng
/// người dùng thì cái đắt là gọi nó quá thường, không phải gọi nó muộn.
/// </para>
///
/// <para>
/// <b>Vì sao điều kiện bản đồ KHÔNG đủ, và vế "ba bảng đứng trước" phải có.</b> Bản đồ bao phủ ngã ngũ
/// SỚM hơn hẳn lúc các bảng được chốt: nó lên <c>[RÕ]</c> ngay khi hội thoại kể đủ, còn ba bảng thì phải
/// lần lượt bày ra và chờ người dùng bấm gửi — mỗi bảng vài lượt. Ca thật (dự án Safety Training 9): bản
/// đồ ngã ngũ quanh lượt 40, bảng luồng mãi lượt 44 mới bày, bảng đối tượng lượt 46, bảng báo cáo còn
/// chưa tới; trong khoảng đó lượt chắt lọc chạy ở MỖI lượt (trước lần chốt đầu không có ngưỡng lô) và
/// không lời gọi nào trong số đó dùng được, vì <see cref="InterviewTableGate.Select"/> còn đang nhường
/// cho ba bảng kia nên bảng màn hình không có đường ra hỏi. Cái giá vẫn đúng hai phần cũ: mỗi lượt một
/// lời gọi ~3.5k token, và mười dòng chờ duyệt do model đoán ra TRƯỚC khi bảng đối tượng chốt
/// (<i>Course List</i>, <i>Course Catalog</i>, <i>Course Management</i>, <i>Course Detail</i>… — cùng một
/// thứ gọi bốn tên) nằm lại trong bảng, vì <see cref="ScreenScopeMapBuilder.Merge"/> chỉ được THÊM.
/// </para>
///
/// <para>
/// Con trỏ lượt là <see cref="Project.InterviewScopeHarvestedTurnCount"/> — RIÊNG, không dùng chung với
/// con trỏ của lượt chắt lọc kia: hai nhịp khác nhau mà chung một con trỏ thì lượt chạy dày kéo con trỏ đi
/// trước và lượt chạy thưa không còn quãng nào để gộp. <b>Fail-open</b> như mọi tầng chắt lọc khác: lời gọi
/// lỗi thì giữ bảng cũ + con trỏ đứng yên, lần sau gộp bù.
/// </para>
/// </summary>
public class InterviewScopeService
{
    /// <summary>
    /// Số lượt mới tối thiểu để chạy lại lượt chắt lọc SAU KHI bảng màn hình đã được chốt một lần. Trước
    /// lần chốt đó thì không có ngưỡng nào: hễ đủ điều kiện là gộp, vì bảng sắp được bày ra và một bảng
    /// thiếu màn hình là thứ người dùng đóng dấu "đây là toàn bộ màn hình của ứng dụng" lên.
    /// </summary>
    public const int HarvestBatchThreshold = 10;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public InterviewScopeService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>
    /// Đã tới lúc chắt phạm vi màn hình chưa. Bản thuần dữ liệu — để test và để gọi từ nơi không có entity.
    ///
    /// <para>
    /// Điều kiện bản đồ chép đúng điều kiện của <see cref="ScreenScopeGate.ShouldAsk"/>, TRỪ vế
    /// <c>HasPending</c>: vế đó là HỆ QUẢ của chính lượt này, đòi nó ở đây là tự khoá. Chép chứ không gọi
    /// lại hàm kia cũng vì thế — hai câu hỏi khác nhau ("đã tới lúc chắt chưa" và "đã tới lúc hỏi chưa")
    /// tình cờ có chung phần lớn điều kiện, và cột lại làm một là để lần sau sửa một cái thì cái kia im
    /// lặng đổi theo.
    /// </para>
    ///
    /// <para>
    /// Và một vế mà cổng kia KHÔNG có: <see cref="ScreenScopeGate.PrecedingTablesDone"/>. Cổng bày bảng có
    /// <see cref="InterviewTableGate.Select"/> đứng trên phân xử nên mở sớm không mất gì; lượt chắt lọc thì
    /// không có trọng tài nào — nó chạy ngay khi điều kiện đúng, và mỗi lần chạy sớm là một lời gọi không
    /// ai tiêu thụ cộng thêm mấy dòng phỏng đoán ở lại trong bảng vĩnh viễn.
    /// </para>
    ///
    /// <para>
    /// Ngưỡng lô CHỈ áp sau lần chốt đầu. Trước đó, khoảng từ lúc bản đồ ngã ngũ tới lúc người dùng bấm gửi
    /// bảng thường chỉ vài lượt, và mọi lượt trong khoảng ấy đều có thể lộ ra màn hình mới — hoãn chúng lại
    /// là bày ra một bảng thiếu đúng phần vừa nói tới.
    /// </para>
    /// </summary>
    public static bool ShouldHarvest(string? coverageMap, string? flowMapJson, string? entityMapJson,
        string? reportMapJson, string? screenScopeJson, int harvestedTurns, int totalTurns)
    {
        var pending = totalTurns - harvestedTurns;
        if (pending <= 0)
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        // BA BẢNG ĐỨNG TRƯỚC PHẢI HẾT VIỆC — vế này của riêng lượt chắt lọc, cổng bày bảng không có nó.
        // Xem ScreenScopeGate.PrecedingTablesDone cho lý do đầy đủ; tóm tắt: cổng mở sớm thì Select phân
        // xử và không mất gì, còn lượt chắt lọc chạy sớm thì cái nó đoán ra ở lại vĩnh viễn.
        if (!ScreenScopeGate.PrecedingTablesDone(coverageMap, flowMapJson, entityMapJson, reportMapJson))
            return false;

        var confirmed = ScreenScopeMapBuilder.IsConfirmed(screenScopeJson);

        var ready = confirmed
            ? InterviewTableGate.IsClear(items, InterviewTableGate.Groups.MainFlow)
            : EntityMapGate.CoverageDecided(items)
              && InterviewTableGate.IsSettled(items, InterviewTableGate.Groups.Report);

        if (!ready)
            return false;

        return !confirmed || pending >= HarvestBatchThreshold;
    }

    /// <summary>
    /// Bản đọc từ entity của <see cref="ShouldHarvest(string?, string?, string?, string?, string?, int, int)"/>.
    /// </summary>
    public static bool ShouldHarvest(Project project, int totalTurns)
        => ShouldHarvest(project.RequirementCoverageMap, project.FlowMap, project.EntityMap, project.ReportMap,
            project.ScreenScopeMap, project.InterviewScopeHarvestedTurnCount, totalTurns);

    /// <summary>
    /// Gộp phần phạm vi vừa lộ ra vào bảng màn hình nếu đã tới nhịp, rồi trả về số mục vừa được thêm (0 khi
    /// không chạy hoặc không có gì mới). <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK — cột
    /// bảng + con trỏ ghi thẳng lên nó.
    /// </summary>
    public async Task<int> UpdateAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        var total = await _db.AgentConversations.CountAsync(c => c.ProjectId == project.Id, cancellationToken);
        if (!ShouldHarvest(project, total))
            return 0;

        var harvested = project.InterviewScopeHarvestedTurnCount;
        var delta = await _db.AgentConversations
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip(harvested)
            .ToListAsync(cancellationToken);

        if (delta.Count == 0)
            return 0;

        var additions = await DistillAsync(project, delta, ba, model, cancellationToken);
        if (additions == null)
            return 0; // Lỗi gọi / trả rác ⇒ fail-open: giữ bảng cũ + con trỏ cũ, lần sau gộp bù.

        // Merge trả null khi không có gì mới — và lúc đó cột bảng KHÔNG bị đụng tới: một quãng hội thoại
        // không làm phát sinh màn hình nào thì không có lý do gì để ghi lại bảng. Con trỏ vẫn phải tiến,
        // nếu không lô sau lại gộp đúng quãng vừa đọc.
        var merged = ScreenScopeMapBuilder.Merge(project.ScreenScopeMap, additions);
        if (merged != null)
            project.ScreenScopeMap = JsonSerializer.Serialize(merged);

        project.InterviewScopeHarvestedTurnCount = harvested + delta.Count;
        await _db.SaveChangesAsync(cancellationToken);
        return merged == null ? 0 : additions.Count;
    }

    private async Task<List<ScopeAddition>?> DistillAsync(Project project, List<AgentConversation> turns, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        // BẢNG MÀN HÌNH ĐANG CÓ, kể cả phần chưa ai rà: đây là thứ quyết định lời đáp rỗng hay không, nên
        // nó phải đầy đủ tới từng chức năng. Thiếu nó thì model nhả ra chính những màn hình đã có bằng chữ
        // khác đi, và mỗi mục như thế là một dòng chờ duyệt giả người dùng phải tự tay bỏ tích.
        sb.AppendLine("## Bảng màn hình đang có (CHỈ nêu thứ KHÔNG có trong đây)");
        sb.AppendLine(RenderScopeState(project.ScreenScopeMap));
        sb.AppendLine();
        sb.AppendLine("## Các lượt hội thoại cần gộp");
        foreach (var t in turns)
            sb.AppendLine($"- {ConversationTurnRenderer.Render(t)}");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/interview-scope.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var (callResult, structured) = await _llm.ChatStructuredAsync<InterviewScope>(
            model, messages, ba.Temperature, new ModelCallLogContext(project.Id, ba, "BAInterviewScope"),
            cancellationToken: cancellationToken);

        if (!callResult.IsSuccess)
            return null;

        return structured?.ScopeAdditions ?? new List<ScopeAddition>();
    }

    /// <summary>Bảng màn hình đang lưu, dạng phẳng để model đối chiếu: mỗi màn một dòng, chức năng đi kèm.</summary>
    private static string RenderScopeState(string? screenScopeJson)
    {
        var rows = ScreenScopeMapBuilder.Parse(screenScopeJson);
        if (rows.Count == 0)
            return "(chưa có màn hình nào)";

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            // Dòng người dùng đã BỎ TÍCH vẫn phải kể ra: model không biết nó tồn tại thì lần nào cũng đề
            // xuất lại đúng màn hình ấy. Merge sẽ chặn (bia), nhưng mỗi lần chặn là một mục vô ích trong
            // output — và một mục vô ích lặp lại đủ lâu sẽ kéo theo cả những mục cạnh nó.
            var mark = row.Included ? string.Empty : " [người dùng đã LOẠI — không đề xuất lại]";
            var functions = row.Functions.Where(f => f.Included).Select(f => f.Name).ToList();
            sb.AppendLine($"- {row.Screen}{mark}"
                + (functions.Count > 0 ? $" | chức năng: {string.Join(", ", functions)}" : string.Empty));
        }

        return sb.ToString().TrimEnd();
    }
}
