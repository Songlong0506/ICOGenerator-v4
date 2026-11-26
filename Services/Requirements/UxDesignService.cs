using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Lượt THIẾT KẾ UX: từ bảng luồng + bảng báo cáo + hội thoại ra một <see cref="UxBlueprint"/> đầy đủ, lưu
/// thẳng vào <c>Project.UxBlueprint</c>. Thay hẳn chuỗi cũ "chắt màn hình → soạn bảng màn hình → xếp chỗ bước
/// mồ côi" — chuỗi đó CHẮT tên màn hình theo cách người dùng kể rồi chỉ được thêm, không bước nào nhìn cả ứng
/// dụng để quyết cấu trúc.
///
/// <para><b>Bốn bước, mỗi bước một trách nhiệm:</b></para>
/// <list type="number">
///   <item><b>Danh mục</b> (BA, <c>ux-inventory.v1.md</c>): đối tượng + vòng đời + tác vụ. Code cấp ID ổn định,
///   gieo tác vụ báo cáo, đối chiếu TỪNG bước luồng (<see cref="UxInventoryBuilder"/>); bước còn mồ côi được hỏi
///   lại đúng một lần, rồi có lưới an toàn tất định.</item>
///   <item><b>Thiết kế</b> (UiUx, <c>UiUx/ux-blueprint.v1.md</c>): màn hình theo pattern library + điều hướng
///   theo vai, nhận đầu vào JSON (<see cref="UxDesignInputBuilder"/>).</item>
///   <item><b>Phản biện</b>: <see cref="UxBlueprintLint"/> chấm; còn lỗi thì đưa đúng danh sách lỗi lại cho model
///   sửa (tối đa <see cref="MaxRevisions"/> vòng, giữ bản ít lỗi nhất).</item>
///   <item><b>Vá + đo</b>: <see cref="UxBlueprintBuilder.Repair"/> lấp phần còn sót, rồi tính hành trình, chỉ số và
///   phát hiện còn lại — lưu cùng bản thiết kế để mọi tầng sau (spec, POC, eval) đọc được.</item>
/// </list>
///
/// <para>
/// <b>Ba chế độ</b> (<see cref="UxDesignMode"/>): <c>design</c> trước khi Brief được duyệt — tái cấu trúc tự do;
/// <c>patch</c> sau khi duyệt — giữ cấu trúc, chỉ xếp chỗ phần mới (người dùng đã duyệt một Brief liệt kê các
/// màn này); <c>feedback</c> — sửa theo góp ý thiết kế ở POC Review (<see cref="RedesignFromFeedbackAsync"/>).
/// </para>
///
/// <para>
/// <b>Chạy ở bước "Write Requirement"</b> (<see cref="DesignPreparationService"/>), không trong buổi phỏng vấn:
/// tới lúc đó phạm vi đã ngã ngũ nên một lần thiết kế đọc trọn hội thoại là đủ, khỏi thiết kế lại mỗi lần phạm vi
/// trôi giữa buổi. Lời gọi danh mục hay thiết kế lỗi ⇒ không ghi gì và trả <see cref="UxDesignOutcome.Failed"/>
/// (caller dừng, không soạn Brief thiếu màn hình). Vòng phản biện lỗi ⇒ dùng bản tốt nhất đang có.
/// </para>
/// </summary>
public class UxDesignService
{
    public const string InventoryPromptKey = "BusinessAnalyst/ux-inventory.v1.md";
    public const string BlueprintPromptKey = "UiUx/ux-blueprint.v1.md";

    /// <summary>Số vòng phản biện tối đa sau lượt thiết kế đầu (mỗi vòng thêm một lời gọi vào bước "Write Requirement").</summary>
    public const int MaxRevisions = 2;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public UxDesignService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>
    /// Đưa bản thiết kế về khớp hội thoại trước khi soạn Brief — gọi từ bước "Write Requirement"
    /// (<see cref="DesignPreparationService"/>). <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK.
    /// <list type="bullet">
    ///   <item>Bản đang lưu còn đúng (<see cref="UxDesignGate.IsStale"/> = false) ⇒ không gọi model lần nào.</item>
    ///   <item>Chưa thiết kế ⇒ đủ bốn bước.</item>
    ///   <item>Đã thiết kế ⇒ chỉ chạy lượt DANH MỤC ở chế độ "chỉ bổ sung" (giữ ID tác vụ). Danh mục không đổi
    ///   (không tác vụ mới, không tác vụ nào mất nguồn) ⇒ dừng ở đó, bản thiết kế giữ nguyên; đổi ⇒ chạy tiếp
    ///   lượt UiUx để xếp chỗ phần mới.</item>
    /// </list>
    /// </summary>
    public async Task<UxDesignOutcome> PrepareAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        var turns = await LoadTurnsAsync(project.Id, cancellationToken);
        if (!UxDesignGate.IsStale(project, turns.Count))
            return UxDesignOutcome.Unchanged;

        var current = UxBlueprintBuilder.Parse(project.UxBlueprint);
        var inventory = await BuildInventoryAsync(project, current, turns, ba, model, cancellationToken);
        if (inventory == null)
            return UxDesignOutcome.Failed;

        if (UxBlueprintBuilder.IsDesigned(project.UxBlueprint) && SameInventory(current, inventory))
        {
            // Lượt danh mục có thể vừa ghi bước NGOÀI PHẦN MỀM lên bảng luồng — vẫn phải lưu cùng con trỏ.
            project.UxDesignedTurnCount = turns.Count;
            await _db.SaveChangesAsync(cancellationToken);
            return UxDesignOutcome.Unchanged;
        }

        var designed = await DesignAsync(project, ba, model, UxDesignGate.Mode(project), current, inventory, turns.Count, cancellationToken);
        return designed ? UxDesignOutcome.Designed : UxDesignOutcome.Failed;
    }

    /// <summary>
    /// Danh mục mới không thêm, không bớt tác vụ nào so với bản đang lưu (so theo ID — ID ổn định qua mọi lần
    /// dựng danh mục, <see cref="UxInventoryBuilder.FindSame"/>), mọi tác vụ đều đã có chỗ, và không tác vụ nào
    /// được gộp thêm người làm hay bước luồng (người làm mới có thể cần vào màn đặt tác vụ — lượt thiết kế lo).
    /// </summary>
    internal static bool SameInventory(UxBlueprint current, UxBlueprint inventory)
    {
        if (inventory.Tasks.Any(t => !t.Placed) || inventory.Tasks.Count != current.Tasks.Count)
            return false;

        foreach (var task in inventory.Tasks)
        {
            var old = current.Tasks.FirstOrDefault(t => UxBlueprintBuilder.SameId(t.Id, task.Id));
            if (old == null
                || !SameSet(old.Actors, task.Actors)
                || !SameSet(old.FlowSteps, task.FlowSteps))
                return false;
        }
        return true;
    }

    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b)
        => a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);

    /// <summary>
    /// Thiết kế lại theo góp ý THIẾT KẾ người dùng gửi từ POC Review. Góp ý được lưu vào
    /// <see cref="UxBlueprint.DesignNotes"/> trước — để mọi lần thiết kế sau còn nhớ — rồi chạy chế độ
    /// <c>feedback</c>. Trả true khi bản thiết kế đã được ghi lại.
    /// </summary>
    public async Task<bool> RedesignFromFeedbackAsync(Project project, Agent ba, IReadOnlyList<UxDesignNote> notes, CancellationToken cancellationToken = default)
    {
        if (notes.Count == 0 || !UxBlueprintBuilder.IsDesigned(project.UxBlueprint))
            return false;

        var current = UxBlueprintBuilder.Parse(project.UxBlueprint);
        current.DesignNotes.AddRange(notes);
        var turns = await LoadTurnsAsync(project.Id, cancellationToken);
        var model = ba.AiModel!;
        var inventory = await BuildInventoryAsync(project, current, turns, ba, model, cancellationToken);
        if (inventory == null)
            return false;
        return await DesignAsync(project, ba, model, UxDesignMode.Feedback, current, inventory, turns.Count, cancellationToken);
    }

    private Task<List<AgentConversation>> LoadTurnsAsync(Guid projectId, CancellationToken cancellationToken)
        => _db.AgentConversations
            .AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

    private async Task<bool> DesignAsync(
        Project project, Agent ba, AiModel model, string mode, UxBlueprint current, UxBlueprint inventory, int turnCount,
        CancellationToken cancellationToken)
    {

        var roles = UxRoles.Collect(project.FlowMap, inventory.Tasks, project.PermissionMatrix);
        var (designer, designerModel) = await ResolveDesignerAsync(ba, model, cancellationToken);
        var hasCurrent = current.Screens.Count > 0;
        var input = UxDesignInputBuilder.Build(
            inventory, project.FlowMap, project.ReportMap, project.PermissionMatrix, roles, mode, hasCurrent ? current : null);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get(BlueprintPromptKey)),
            new(ChatRole.User, RenderDesignRequest(project, input))
        };

        var (call, draft) = await _llm.ChatStructuredAsync<UxBlueprintDraft>(
            designerModel, messages, new ModelCallLogContext(project.Id, designer, "UiUxBlueprint"),
            cancellationToken: cancellationToken);
        if (!call.IsSuccess)
            return false;

        var best = UxBlueprintBuilder.FromDraft(draft, inventory, roles, hasCurrent ? current : null);
        var bestErrors = UxBlueprintLint.Errors(UxBlueprintLint.Check(best, project.FlowMap));

        // VÒNG PHẢN BIỆN: nối thêm (bản vừa trả + danh sách lỗi) thay vì dựng lại prompt — tiền tố dài giữ
        // nguyên nên endpoint đọc lại nó từ cache. Giữ bản ÍT lỗi nhất: một vòng sửa làm hỏng thêm chỗ khác
        // không được phép thay bản tốt hơn.
        for (var round = 1; round <= MaxRevisions && bestErrors.Count > 0; round++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, call.Content ?? JsonSerializer.Serialize(draft)));
            messages.Add(new ChatMessage(ChatRole.User, RevisionDemand(bestErrors)));

            var (revisionCall, revised) = await _llm.ChatStructuredAsync<UxBlueprintDraft>(
                designerModel, messages, new ModelCallLogContext(project.Id, designer, "UiUxBlueprintRevision"),
                cancellationToken: cancellationToken);
            if (!revisionCall.IsSuccess || revised == null)
                break;

            var candidate = UxBlueprintBuilder.FromDraft(revised, inventory, roles, hasCurrent ? current : null);
            var candidateErrors = UxBlueprintLint.Errors(UxBlueprintLint.Check(candidate, project.FlowMap));
            call = revisionCall;
            draft = revised;
            if (candidateErrors.Count <= bestErrors.Count && candidate.Screens.Count > 0)
            {
                best = candidate;
                bestErrors = candidateErrors;
            }
        }

        // Model không trả nổi màn nào ⇒ không ghi gì (giữ bản đang có; caller báo lỗi). Dựng cả ứng
        // dụng từ lưới an toàn của Repair là chốt một thiết kế không ai thiết kế.
        if (best.Screens.Count == 0)
            return false;

        UxBlueprintBuilder.Repair(best, roles);
        best.Journeys = UxJourneyAnalyzer.Journeys(best, project.FlowMap);
        best.Findings = UxBlueprintLint.Check(best, project.FlowMap);
        best.Metrics = UxJourneyAnalyzer.Metrics(best, project.FlowMap);
        best.Version = current.Version + 1;
        best.DesignNotes = current.DesignNotes;

        project.UxBlueprint = UxBlueprintBuilder.Serialize(best);

        // Bảng phân quyền khoá theo ID tác vụ: màn hình đổi tên/gộp/tách thì chỉ cột hiển thị đổi theo, và dòng
        // của tác vụ đã bỏ đi theo tác vụ đó. Dòng cho tác vụ MỚI do lượt soạn phân quyền bổ sung (PermissionMatrixGate).
        var rebound = PermissionMatrixBuilder.Rebind(project.PermissionMatrix, best);
        if (rebound != null)
            project.PermissionMatrix = rebound;

        // Lượt thiết kế đọc TRỌN hội thoại ⇒ bấm lại "Write Requirement" mà không có lượt mới thì không thiết kế lại.
        project.UxDesignedTurnCount = turnCount;

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ==== bước 1: danh mục ====

    private async Task<UxBlueprint?> BuildInventoryAsync(
        Project project, UxBlueprint current, IReadOnlyList<AgentConversation> turns,
        Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get(InventoryPromptKey)),
            new(ChatRole.User, RenderInventoryInput(project, current, turns))
        };

        var (call, draft) = await _llm.ChatStructuredAsync<UxInventoryDraft>(
            model, messages, new ModelCallLogContext(project.Id, ba, "BAUxInventory"), cancellationToken: cancellationToken);
        if (!call.IsSuccess)
            return null;

        var (objects, tasks) = UxInventoryBuilder.Build(current, draft, project.FlowMap, project.ReportMap);
        var outside = new List<UxOutsideStep>();
        var uncovered = Collect(tasks, draft, outside, project.FlowMap);

        // Bước còn mồ côi ⇒ hỏi lại ĐÚNG các bước đó một lần (mỗi bước: tác vụ nào, hay làm ngoài phần mềm).
        if (uncovered.Count > 0)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, call.Content ?? "{}"));
            messages.Add(new ChatMessage(ChatRole.User, OrphanDemand(uncovered, project.FlowMap)));
            var (retry, more) = await _llm.ChatStructuredAsync<UxInventoryDraft>(
                model, messages, new ModelCallLogContext(project.Id, ba, "BAUxInventoryOrphans"), cancellationToken: cancellationToken);
            if (retry.IsSuccess && more != null)
            {
                var merged = new UxBlueprint { Objects = objects, Tasks = tasks, LastTaskNumber = current.LastTaskNumber };
                (objects, tasks) = UxInventoryBuilder.Build(merged, more, project.FlowMap, project.ReportMap);
                uncovered = Collect(tasks, more, outside, project.FlowMap);
            }
        }

        // Lưới an toàn: một bước người dùng đã chốt không bao giờ được rơi khỏi ứng dụng.
        UxInventoryBuilder.AddFallbackTasks(tasks, uncovered, project.FlowMap, current.LastTaskNumber);

        var marked = FlowMapBuilder.MarkOutsideSoftware(project.FlowMap, outside);
        if (marked != null)
            project.FlowMap = marked;

        return new UxBlueprint
        {
            Objects = objects,
            Tasks = tasks,
            LastTaskNumber = Math.Max(current.LastTaskNumber, UxInventoryBuilder.NextNumber(tasks.Select(t => t.Id)) - 1),
            LastScreenNumber = current.LastScreenNumber,
            DesignNotes = current.DesignNotes,
            Version = current.Version
        };
    }

    // Bước còn mồ côi sau khi trừ các bước model khai là làm ngoài phần mềm (gom dồn vào `outside`).
    private static List<string> Collect(List<UxTask> tasks, UxInventoryDraft? draft, List<UxOutsideStep> outside, string? flowMapJson)
    {
        var uncovered = UxInventoryBuilder.UncoveredSteps(tasks, flowMapJson);
        foreach (var step in UxInventoryBuilder.ResolveOutside(draft?.OutsideSoftware, uncovered))
        {
            if (!outside.Any(o => o.Step == step.Step))
                outside.Add(step);
        }
        return uncovered.Where(u => !outside.Any(o => o.Step == u)).ToList();
    }

    private static string RenderInventoryInput(Project project, UxBlueprint current, IReadOnlyList<AgentConversation> turns)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Các bước của bảng luồng đã chốt (MỖI bước phải có ÍT NHẤT MỘT tác vụ nhận vào `flowSteps`, hoặc nằm ở `outsideSoftware`)");
        var steps = UxInventoryBuilder.FlowSteps(project.FlowMap);
        if (steps.Count == 0)
            sb.AppendLine("(chưa có)");
        foreach (var flow in steps.GroupBy(s => s.Flow))
        {
            sb.AppendLine($"* Luồng «{flow.Key}» [{FlowKind.Label(flow.First().Kind)}]");
            foreach (var step in flow)
                sb.AppendLine($"  - người làm: {step.Actor} · bước: {step.Action}" + (step.Outcome.Length > 0 ? $" · sau bước: {step.Outcome}" : string.Empty));
        }

        if (current.Tasks.Count > 0 || current.Objects.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Danh mục ĐANG CÓ (giữ nguyên — chỉ BỔ SUNG đối tượng/tác vụ còn thiếu, không nêu lại thứ đã có)");
            foreach (var obj in current.Objects)
                sb.AppendLine($"* đối tượng: {obj.Name}" + (obj.IsCatalog ? " [danh mục]" : string.Empty)
                    + (obj.States.Count > 0 ? $" · trạng thái: {string.Join(" → ", obj.States)}" : string.Empty));
            foreach (var task in current.Tasks.Where(t => t.Kind != UxTaskKind.Report))
                sb.AppendLine($"* tác vụ: {task.Name} · đối tượng: {task.Object} · loại: {task.Kind}" + (task.Actors.Count > 0 ? $" · người làm: {string.Join(", ", task.Actors)}" : string.Empty));
        }

        var reports = ReportMapBuilder.RenderConfirmedBlock(project.ReportMap);
        if (!string.IsNullOrWhiteSpace(reports))
        {
            sb.AppendLine();
            sb.AppendLine("## Bảng báo cáo (hệ thống TỰ gieo mỗi báo cáo thành một tác vụ — KHÔNG nêu báo cáo trong `tasks`)");
            sb.AppendLine(reports.Trim());
        }

        var coverage = CoverageMapParser.Parse(project.RequirementCoverageMap);
        foreach (var (key, title) in new[]
                 {
                     (CoverageGroupKey.Roles, "Đối tượng người dùng & vai trò"),
                     (CoverageGroupKey.Data, "Dữ liệu / danh mục chính"),
                     (CoverageGroupKey.Lifecycle, "Vòng đời & trạng thái"),
                     (CoverageGroupKey.Rules, "Quy tắc nghiệp vụ")
                 })
        {
            var known = coverage.FirstOrDefault(x => x.Key == key)?.Known ?? Array.Empty<string>();
            if (known.Count == 0)
                continue;
            sb.AppendLine();
            sb.AppendLine($"## Điều đã ghi nhận về «{title}»");
            foreach (var k in known)
                sb.AppendLine("- " + k);
        }

        sb.AppendLine();
        sb.AppendLine("## Hội thoại");
        foreach (var t in turns.Where(t => !ConversationTurnRenderer.CarriesNoRequirement(t)))
            sb.AppendLine($"- {ConversationTurnRenderer.Render(t)}");

        return sb.ToString();
    }

    private static string OrphanDemand(IReadOnlyList<string> uncovered, string? flowMapJson)
    {
        var steps = UxInventoryBuilder.FlowSteps(flowMapJson);
        var sb = new StringBuilder();
        sb.AppendLine("Các bước luồng sau CHƯA có tác vụ nào nhận. Với MỖI bước: hoặc thêm một tác vụ (trong `tasks`, chép đúng chữ của bước vào `flowSteps`), hoặc khai nó ở `outsideSoftware` nếu con người làm nó bên ngoài ứng dụng. Chỉ trả các tác vụ MỚI cho các bước này — không nêu lại tác vụ đã có.");
        foreach (var action in uncovered)
        {
            var step = steps.FirstOrDefault(s => s.Action == action);
            sb.AppendLine($"- bước: {action}" + (step != null ? $" (người làm: {step.Actor}; sau bước: {(step.Outcome.Length > 0 ? step.Outcome : "-")})" : string.Empty));
        }
        return sb.ToString();
    }

    // ==== bước 2–3: thiết kế + phản biện ====

    private static string RenderDesignRequest(Project project, UxDesignInput input)
        => RenderDesignRequest(project.Name, project.Description, input);

    /// <summary>
    /// Thông điệp người dùng của lượt thiết kế: dự án, chế độ, rồi khối JSON. Public để golden set của Prompt
    /// Evals (<c>UxGoldenCases</c>) dựng ĐÚNG định dạng production.
    /// </summary>
    public static string RenderDesignRequest(string projectName, string? description, UxDesignInput input)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Dự án: {projectName}");
        if (!string.IsNullOrWhiteSpace(description))
            sb.AppendLine($"Mô tả: {OneLine(description)}");
        sb.AppendLine();
        sb.AppendLine(input.Mode switch
        {
            UxDesignMode.Patch => "Chế độ: PATCH — người dùng đã duyệt tài liệu liệt kê các màn hình hiện có. GIỮ NGUYÊN cấu trúc (tên màn, dạng màn, menu); chỉ xếp chỗ các tác vụ `placed: false` vào màn đúng nhất. Chỉ đổi cấu trúc khi không còn cách nào đặt tác vụ mới cho hợp lý.",
            UxDesignMode.Feedback => "Chế độ: FEEDBACK — người dùng vừa góp ý về THIẾT KẾ ở bản demo (mục `designNotes`). Sửa đúng những gì góp ý yêu cầu; giữ nguyên phần còn lại.",
            _ => "Chế độ: DESIGN — thiết kế tổng thể. Có `currentScreens` thì đó là bản trước: giữ tên/ID những màn vẫn đúng, tái cấu trúc thoải mái nơi pattern library chỉ ra cách tốt hơn."
        });
        sb.AppendLine();
        sb.AppendLine(UxDesignInputBuilder.Render(input));
        return sb.ToString();
    }

    public static string RevisionDemand(IReadOnlyList<UxFinding> errors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Bản thiết kế vừa rồi còn các LỖI dưới đây (do hệ thống kiểm tự động theo pattern library). Sửa HẾT, rồi trả lại TOÀN BỘ bản thiết kế (mọi màn hình + điều hướng) theo đúng định dạng cũ — không chỉ phần sửa.");
        foreach (var error in errors)
            sb.AppendLine($"- [{error.Code}] {error.Message}");
        return sb.ToString();
    }

    /// <summary>Model của lượt thiết kế: agent UiUx nếu đã cấu hình model, không thì chính BA.</summary>
    private async Task<(Agent Agent, AiModel Model)> ResolveDesignerAsync(Agent ba, AiModel fallback, CancellationToken cancellationToken)
    {
        var designer = await _db.Agents
            .Include(a => a.AiModel)
            .Where(a => a.RoleKey == AgentRoleKey.UiUx)
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return designer?.AiModel != null ? (designer, designer.AiModel) : (ba, fallback);
    }

    private static string OneLine(string? value) => RequirementText.OneLine(value);
}

/// <summary>Kết quả của <see cref="UxDesignService.PrepareAsync"/>.</summary>
public enum UxDesignOutcome
{
    /// <summary>Bản đang lưu vẫn khớp hội thoại — không đổi gì.</summary>
    Unchanged,

    /// <summary>Bản thiết kế vừa được dựng/bổ sung và lưu.</summary>
    Designed,

    /// <summary>Lời gọi danh mục/thiết kế lỗi, hoặc model không trả nổi màn nào — không ghi gì.</summary>
    Failed
}
