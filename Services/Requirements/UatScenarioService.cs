using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Sinh bộ kịch bản UAT từ AI Design Spec TRƯỚC khi Developer dựng POC, lưu tại
/// <c>04_Implementation/uat-scenarios.json</c> cạnh poc-demo.html. Cùng một bộ kịch bản phục vụ hai phía:
/// là đích máy móc của POC (nối vào prompt dựng POC, cổng <see cref="Artifacts.PocUatCoverage"/>), và là
/// HƯỚNG DẪN SỬ DỤNG mà trang POC Review bày cạnh bản demo — người dùng làm theo từng bước, bấm vào một
/// bước thì POC chỉ chỗ, bước nào sai thì ghim luôn ghi chú cho Developer. FAIL-OPEN toàn phần: sinh
/// lỗi/không có BA ⇒ bỏ qua, trang review chỉ đơn giản không có panel hướng dẫn.
/// </summary>
public class UatScenarioService
{
    public const string FileName = "uat-scenarios.json";

    /// <summary>Trần số kịch bản của một bộ — lượt lái thật (<see cref="Artifacts.PlaywrightPocRuntimeChecker"/>) lái HẾT số này.</summary>
    public const int MaxScenarios = 8;

    // Trần thao tác của MỘT bước: một bước là một việc người dùng làm ("mở danh sách rồi bấm dòng X", "nhập
    // ba trường rồi Lưu"), vượt mức này là model đang nhồi cả kịch bản vào một bước.
    private const int MaxActionsPerStep = 8;

    // Relaxed để tiếng Việt và dấu nháy giữ nguyên thay vì \uXXXX — file này có người mở ra đọc.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly WorkspacePathResolver _workspacePathResolver;
    private readonly BAAgentResolver _agentResolver;
    private readonly ILogger<UatScenarioService> _logger;

    public UatScenarioService(
        AppDbContext db,
        ILlmClient llm,
        PromptTemplateService prompts,
        WorkspacePathResolver workspacePathResolver,
        BAAgentResolver agentResolver,
        ILogger<UatScenarioService> logger)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _workspacePathResolver = workspacePathResolver;
        _agentResolver = agentResolver;
        _logger = logger;
    }

    /// <summary>
    /// Sinh và ghi file kịch bản cho project (ghi đè bản cũ — spec là nguồn duy nhất nên chạy lại là
    /// làm tươi). Mọi lỗi đều được nuốt + log: đây là bước phụ trợ, không được làm fail task POC.
    /// </summary>
    public async Task TryGenerateAsync(Guid projectId, string aiDesignSpec, Guid? workflowRunId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(aiDesignSpec))
                return;

            var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
            if (ba == null)
                return;
            var model = ba.AiModel!;

            var project = await _db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
            if (project == null)
                return;

            var prompt = _prompts.Get("BusinessAnalyst/uat-scenarios.v1.md")
                .Replace("{{input}}", aiDesignSpec);

            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

            var (callResult, structured) = await _llm.ChatStructuredAsync<UatScenarioDraftSet>(
                model, messages, new ModelCallLogContext(projectId, ba, "BAUatScenarios", workflowRunId),
                cancellationToken: cancellationToken);

            if (!callResult.IsSuccess)
            {
                _logger.LogWarning("UAT scenario generation failed for project {ProjectId}: {Error}", projectId, callResult.ErrorMessage ?? callResult.Content);
                return;
            }

            var set = Sanitize(structured is { Scenarios.Count: > 0 } ? FromDraft(structured) : ParseFallback(callResult.Content));
            if (set.Scenarios.Count == 0)
                return;

            var path = GetScenarioPath(project.Id, project.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, ToJson(set), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown thật sự thì để caller xử lý như mọi bước khác.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not generate UAT scenarios for project {ProjectId}.", projectId);
        }
    }

    /// <summary>Dạng JSON ghi ra <see cref="FileName"/> — thụt dòng, giữ nguyên tiếng Việt.</summary>
    public static string ToJson(UatScenarioSet set) => JsonSerializer.Serialize(set, WriteOptions);

    /// <summary>
    /// Khối văn bản mô tả bộ kịch bản nghiệm thu để NỐI vào prompt dựng POC. Đây là điểm khác biệt của
    /// hướng "test-first": Developer agent biết TRƯỚC mình sẽ bị nghiệm thu bằng những đường đi nào, và
    /// được yêu cầu đặt tên các mục <c>window.pocScenarios()</c> đúng bằng tiêu đề kịch bản để cổng
    /// <see cref="Artifacts.PocUatCoverage"/> đối chiếu máy móc được. Bộ rỗng ⇒ chuỗi rỗng (prompt như cũ).
    /// </summary>
    public static string BuildPromptBlock(UatScenarioSet? set)
    {
        var scenarios = set?.Scenarios ?? new List<UatScenario>();
        if (scenarios.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("# KỊCH BẢN NGHIỆM THU (UAT) — ĐÍCH BẮT BUỘC CỦA POC NÀY");
        sb.AppendLine();
        sb.AppendLine("Đây là các kịch bản do BA sinh từ chính AI Design Spec ở trên, TRƯỚC khi bạn dựng POC. Người dùng sẽ mở POC và bấm thử ĐÚNG các bước này để nghiệm thu, và cổng AuditPocContent đối chiếu MÁY MÓC:");
        sb.AppendLine("- MỖI kịch bản dưới đây phải có ĐÚNG MỘT phần tử trong `window.pocScenarios()` với `title` là **nguyên văn tiêu đề kịch bản** (đặt tên khác đi sẽ bị báo là thiếu kịch bản).");
        sb.AppendLine("- Phần tử đó phải PASS THẬT: `pass` được tính bằng cách gọi chính các hàm nghiệp vụ/điều hướng của POC theo đúng trình tự các bước, rồi so trạng thái quan sát được với kỳ vọng.");
        if (scenarios.Any(s => s.HasActionPlan))
        {
            sb.AppendLine("- Audit còn LÁI THẬT các bước này trong trình duyệt, làm ĐÚNG các thao tác in sau chữ \"máy làm:\" dưới mỗi bước, theo thứ tự: chọn vai ở VIEW AS, mở màn ở menu trái, bấm điều khiển mang đúng nhãn «…» (nút, tab, liên kết, hoặc dòng bảng có chữ đó), gõ giá trị vào ô có NHÃN «…» (label / placeholder / aria-label của ô), chọn giá trị ở ô chọn có nhãn «…», rồi nhìn chữ cần thấy. Vì vậy:");
            sb.AppendLine("  - Nhãn trên UI phải trùng NGUYÊN VĂN nhãn trong thao tác; ô nhập phải có `<label for>` (hoặc placeholder/aria-label) mang đúng nhãn đó.");
            sb.AppendLine("  - Nút được bấm phải làm thật — bấm mà màn hình không đổi gì bị báo là nút chết. Dòng bảng được bấm phải mở đúng thứ bước kế cần (vd khung/màn chi tiết).");
            sb.AppendLine("  - Thao tác \"nhìn thấy\" không bấm gì: chữ đó phải hiện trên màn đang mở (hoặc modal) sau các thao tác trước — đừng thêm nút mang chữ đó chỉ để máy \"tìm thấy\".");
        }
        else
        {
            sb.AppendLine("- Audit còn LÁI THẬT các bước này trong trình duyệt: nó tìm nút/điều khiển theo nhãn ghi trong bước rồi CLICK. Vì vậy nhãn nút trên UI phải trùng với nhãn nêu trong bước, và nút phải làm thật (bấm mà màn hình không đổi gì sẽ bị báo là nút chết).");
        }
        sb.AppendLine();
        // NEO CHỈ CHỖ: đích thứ hai của khối này. Xem UatAnchor để biết vì sao mã neo phải do một chỗ duy
        // nhất sinh ra, và PocUatAnchors cho cổng đối chiếu.
        sb.AppendLine($"**NEO CHỈ CHỖ (`{UatAnchor.Attribute}`) — BẮT BUỘC cho TỪNG BƯỚC.** Mỗi bước dưới đây in kèm mã neo của nó (`{UatAnchor.Markup(1, 2)}` = bước 3 của kịch bản 2). Gắn mã đó vào phần tử mà bước ấy đụng tới:");
        sb.AppendLine("- Bước THAO TÁC ⇒ chính nút / ô nhập / mục menu người dùng bấm. Bước nhập cả cụm trường (\"nhập đầy đủ thông tin…\") ⇒ thẻ `<form>` hoặc khối bao quanh cụm ô đó.");
        sb.AppendLine("- Bước KIỂM TRA ⇒ phần tử HIỂN THỊ kết quả cần đối chiếu (ô trạng thái, badge, dòng bảng, chỗ in mã tự sinh) — không bỏ trống vì \"bước này không bấm gì\".");
        sb.AppendLine($"- Một phần tử phục vụ nhiều bước thì ghi nhiều mã cách nhau bằng dấu cách: `{UatAnchor.Attribute}=\"1.4 3.2\"`.");
        sb.AppendLine("- Vì sao bắt buộc: trang POC Review cho người nghiệm thu bấm vào một bước rồi TÔ SÁNG đúng phần tử mang mã neo đó, và lượt lái tự động click theo mã neo thay vì đoán theo nhãn chữ. Thiếu mã nào thì AuditPocContent báo đúng bước đó còn thiếu.");
        sb.AppendLine();

        for (var i = 0; i < scenarios.Count; i++)
        {
            var s = scenarios[i];
            sb.AppendLine($"{i + 1}. **{s.Title}**"
                + (string.IsNullOrWhiteSpace(s.Screen) ? "" : $" — màn hình: {s.Screen}")
                + (s.RuleRefs.Count > 0 ? $" — kiểm rule: {string.Join(", ", s.RuleRefs)}" : ""));
            for (var j = 0; j < s.Steps.Count; j++)
            {
                sb.AppendLine($"   - `{UatAnchor.Markup(i, j)}` — {s.Steps[j]}");
                var actions = s.ActionsOf(j);
                if (actions.Count > 0)
                    sb.AppendLine($"     máy làm: {string.Join(" → ", actions.Select(UatActionKind.Describe))}");
            }
        }

        return sb.ToString();
    }

    /// <summary>Đọc bộ kịch bản đã lưu của project; không có/hỏng ⇒ bộ rỗng (trang review tự ẩn panel).</summary>
    public async Task<UatScenarioSet> LoadAsync(Guid projectId, string projectName, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = GetScenarioPath(projectId, projectName);
            if (!File.Exists(path))
                return new UatScenarioSet();

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return Sanitize(JsonSerializer.Deserialize<UatScenarioSet>(json, LlmJson.Options) ?? new UatScenarioSet());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read UAT scenarios for project {ProjectId}.", projectId);
            return new UatScenarioSet();
        }
    }

    private string GetScenarioPath(Guid projectId, string projectName)
    {
        var mockupPath = _workspacePathResolver.GetMockupPath(WorkspacePathResolver.GetWorkspaceFolder(projectId, projectName));
        return Path.Combine(Path.GetDirectoryName(mockupPath)!, FileName);
    }

    // Đường lùi khi lời gọi structured không bóc được đối tượng: thử dạng mới (bước là đối tượng có
    // actions), rồi dạng cũ (bước là chuỗi — model phớt lờ định dạng, hoặc prompt bị Prompt Studio ghi đè
    // bằng bản cũ). Dạng cũ vẫn dùng được: lượt lái lùi về đoán theo câu chữ.
    internal static UatScenarioSet ParseFallback(string? raw)
    {
        var draft = LlmJson.TryDeserialize<UatScenarioDraftSet>(raw);
        if (draft is { Scenarios.Count: > 0 })
            return FromDraft(draft);
        return LlmJson.TryDeserialize<UatScenarioSet>(raw) ?? new UatScenarioSet();
    }

    // Bản nháp của model mang câu chữ và thao tác CÙNG một đối tượng bước — thẳng hàng theo cấu trúc. Lưu
    // thì tách thành Steps (người đọc, mọi chỗ cũ vẫn dùng) + StepActions song song theo chỉ số.
    private static UatScenarioSet FromDraft(UatScenarioDraftSet draft) => new()
    {
        Scenarios = (draft.Scenarios ?? new List<UatScenarioDraft>()).Where(d => d != null).Select(d => new UatScenario
        {
            Title = d.Title ?? string.Empty,
            Screen = d.Screen ?? string.Empty,
            RuleRefs = d.RuleRefs ?? new List<string>(),
            Steps = (d.Steps ?? new List<UatStepDraft>()).Select(x => x?.Text ?? string.Empty).ToList(),
            StepActions = (d.Steps ?? new List<UatStepDraft>()).Select(x => x?.Actions ?? new List<UatAction>()).ToList()
        }).ToList()
    };

    // Chặn dữ liệu rác của model: bỏ kịch bản không tên/không bước, giới hạn số lượng, bỏ thao tác không
    // hiểu được. Câu chữ và thao tác được lọc THEO CẶP để StepActions[j] luôn là của Steps[j]; bộ kịch bản
    // không có kế hoạch cho đủ mọi bước (file cũ) thì bỏ hẳn kế hoạch — kế hoạch lệch bước nguy hiểm hơn
    // không có kế hoạch.
    internal static UatScenarioSet Sanitize(UatScenarioSet set)
    {
        set.Scenarios = (set.Scenarios ?? new List<UatScenario>())
            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Title) && (s.Steps ?? new List<string>()).Any(x => !string.IsNullOrWhiteSpace(x)))
            .Take(MaxScenarios)
            .ToList();
        foreach (var s in set.Scenarios)
        {
            s.Title = s.Title.Trim();
            s.Screen = (s.Screen ?? string.Empty).Trim();
            s.RuleRefs = (s.RuleRefs ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();

            var steps = s.Steps ?? new List<string>();
            var plans = s.StepActions ?? new List<List<UatAction>>();
            var aligned = plans.Count == steps.Count;
            var kept = steps
                .Select((text, j) => (Text: text, Actions: aligned ? plans[j] : null))
                .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                .ToList();
            s.Steps = kept.Select(x => x.Text.Trim()).ToList();
            s.StepActions = aligned
                ? kept.Select(x => SanitizeActions(x.Actions)).ToList()
                : new List<List<UatAction>>();
        }
        return set;
    }

    private static List<UatAction> SanitizeActions(List<UatAction>? actions) =>
        (actions ?? new List<UatAction>())
            .Where(a => a != null)
            .Select(a => new UatAction
            {
                Kind = (a.Kind ?? string.Empty).Trim().ToLowerInvariant(),
                Target = (a.Target ?? string.Empty).Trim(),
                Value = (a.Value ?? string.Empty).Trim()
            })
            .Where(a => UatActionKind.IsKnown(a.Kind)
                        && a.Target.Length > 0
                        && (!UatActionKind.NeedsValue(a.Kind) || a.Value.Length > 0))
            .Take(MaxActionsPerStep)
            .ToList();

    // ── Hình dạng model trả về ─────────────────────────────────────────────────────────────────────────
    // Tách khỏi contract lưu trữ: với model, câu chữ và thao tác của một bước là MỘT đối tượng (không thể
    // lệch nhau); với file lưu, Steps phải giữ là mảng chuỗi vì mọi chỗ đọc cũ (trang review, neo, cổng)
    // dùng nó.
    private sealed class UatScenarioDraftSet
    {
        public List<UatScenarioDraft>? Scenarios { get; set; }
    }

    private sealed class UatScenarioDraft
    {
        public string? Title { get; set; }
        public string? Screen { get; set; }
        public List<string>? RuleRefs { get; set; }
        public List<UatStepDraft>? Steps { get; set; }
    }

    private sealed class UatStepDraft
    {
        public string? Text { get; set; }
        public List<UatAction>? Actions { get; set; }
    }
}
