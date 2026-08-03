using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Sinh bộ kịch bản UAT từ AI Design Spec ngay sau khi POC dựng xong, lưu tại
/// <c>04_Implementation/uat-scenarios.json</c> cạnh poc-demo.html. Trang POC Review đọc file này để
/// render checklist "đi từng bước" — biến review POC từ khám phá tự do (user thường không biết nên thử
/// gì) thành UAT có kịch bản; bước fail thì ghim luôn ghi chú cho Developer. FAIL-OPEN toàn phần: sinh
/// lỗi/không có BA ⇒ bỏ qua, trang review chỉ đơn giản không có checklist (như trước đây).
/// </summary>
public class UatScenarioService
{
    public const string FileName = "uat-scenarios.json";
    private const int MaxScenarios = 8;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

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

            var (callResult, structured) = await _llm.ChatStructuredAsync<UatScenarioSet>(
                model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAUatScenarios", workflowRunId),
                cancellationToken: cancellationToken);

            if (!callResult.IsSuccess)
            {
                _logger.LogWarning("UAT scenario generation failed for project {ProjectId}: {Error}", projectId, callResult.ErrorMessage ?? callResult.Content);
                return;
            }

            var set = structured ?? ParseFallback(callResult.Content);
            set = Sanitize(set);
            if (set.Scenarios.Count == 0)
                return;

            // Cổng tất định: MỌI câu nghiệm thu người dùng đã duyệt (§ 14 của spec) phải có ít nhất một
            // kịch bản chứng minh. Thiếu thì chạy ĐÚNG MỘT vòng bổ sung — cùng cách RequirementDocsService
            // xử lý màn hình rơi rụng ở biên Brief↔Spec. Fail-open: vòng bổ sung lỗi ⇒ giữ bộ đang có.
            set = await EnsureAcceptanceCoverageAsync(set, aiDesignSpec, prompt, projectId, ba, model, workflowRunId, cancellationToken);

            var path = GetScenarioPath(project.Id, project.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(set, WriteOptions), cancellationToken);
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

    /// <summary>
    /// Các câu nghiệm thu (AC-n) chưa được kịch bản nào trỏ tới. Rỗng khi spec không có mục § 14 hoặc
    /// mọi AC đã được phủ. Tách ra public static để trang POC Review và test dùng chung một định nghĩa
    /// "đã phủ" với vòng bổ sung bên dưới.
    /// </summary>
    public static IReadOnlyList<PocAcceptanceCriterion> FindUncoveredAcceptanceCriteria(PocSpec spec, UatScenarioSet? set)
    {
        if (spec.AcceptanceCriteria.Count == 0)
            return Array.Empty<PocAcceptanceCriterion>();

        var covered = (set?.Scenarios ?? new List<UatScenario>())
            .SelectMany(s => s.AcRefs)
            .Select(NormalizeRef)
            .Where(r => r.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return spec.AcceptanceCriteria
            .Where(ac => !covered.Contains(NormalizeRef(ac.Ref)))
            .ToList();
    }

    /// <summary>"ac 2" / "AC-2" / "AC2" → "AC-2": model viết mã theo nhiều kiểu, so khớp phải bỏ qua khác biệt đó.</summary>
    private static string NormalizeRef(string? value)
    {
        var m = Regex.Match(value ?? string.Empty, @"AC[-\s_]?(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? "AC-" + m.Groups[1].Value : string.Empty;
    }

    // Một vòng bổ sung cho các AC chưa có kịch bản: gửi lại prompt gốc + bộ vừa sinh + danh sách AC còn
    // thiếu, yêu cầu xuất lại TOÀN BỘ bộ kịch bản (giữ nguyên phần đã đạt). Xuất lại cả bộ thay vì xin
    // phần thêm để tránh phải trộn hai bộ có thể mâu thuẫn nhau; MaxScenarios vẫn chặn ở Sanitize.
    private async Task<UatScenarioSet> EnsureAcceptanceCoverageAsync(
        UatScenarioSet set,
        string aiDesignSpec,
        string basePrompt,
        Guid projectId,
        Domain.Agent ba,
        Domain.AiModel model,
        Guid? workflowRunId,
        CancellationToken cancellationToken)
    {
        var spec = PocSpec.Parse(aiDesignSpec);
        var missing = FindUncoveredAcceptanceCriteria(spec, set);
        if (missing.Count == 0)
            return set;

        var sb = new StringBuilder();
        sb.AppendLine(basePrompt);
        sb.AppendLine();
        sb.AppendLine("# BỘ KỊCH BẢN BẠN VỪA SOẠN (chưa đạt)");
        sb.AppendLine(JsonSerializer.Serialize(set, WriteOptions));
        sb.AppendLine();
        sb.AppendLine("# KẾT QUẢ ĐỐI CHIẾU TỰ ĐỘNG");
        sb.AppendLine("Các câu nghiệm thu sau của mục \"## 14. Acceptance Criteria\" CHƯA có kịch bản nào chứng minh (không kịch bản nào ghi mã này trong `acRefs`):");
        foreach (var ac in missing)
            sb.AppendLine($"- {ac.Ref}: {ac.Text}");
        sb.AppendLine();
        sb.AppendLine("Hãy xuất lại TOÀN BỘ bộ kịch bản: GIỮ NGUYÊN các kịch bản đã có, BỔ SUNG kịch bản cho từng câu nghiệm thu còn thiếu ở trên (hoặc thêm mã AC đó vào `acRefs` của một kịch bản đã có nếu kịch bản đó thật sự chứng minh được nó). Vẫn trả JSON đúng format cũ.");

        var (fixCall, fixStructured) = await _llm.ChatStructuredAsync<UatScenarioSet>(
            model, [new ChatMessage(ChatRole.User, sb.ToString())], ba.Temperature,
            new ModelCallLogContext(projectId, ba, "BAUatScenariosAcFix", workflowRunId),
            cancellationToken: cancellationToken);

        if (!fixCall.IsSuccess)
        {
            _logger.LogWarning("UAT acceptance-coverage round failed for project {ProjectId}: {Error}", projectId, fixCall.ErrorMessage ?? fixCall.Content);
            return set;
        }

        var repaired = Sanitize(fixStructured ?? ParseFallback(fixCall.Content));
        // Bộ sửa chỉ được nhận khi nó thật sự phủ nhiều hơn — một bản trả về nghèo hơn (model bỏ bớt
        // kịch bản) sẽ làm bộ nghiệm thu tệ đi thay vì tốt lên.
        if (repaired.Scenarios.Count == 0
            || FindUncoveredAcceptanceCriteria(spec, repaired).Count >= missing.Count)
        {
            return set;
        }

        return repaired;
    }

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
        sb.AppendLine("- Audit còn LÁI THẬT các bước này trong trình duyệt: nó tìm nút/điều khiển theo nhãn ghi trong bước rồi CLICK. Vì vậy nhãn nút trên UI phải trùng với nhãn nêu trong bước, và nút phải làm thật (bấm mà màn hình không đổi gì sẽ bị báo là nút chết).");
        sb.AppendLine();

        for (var i = 0; i < scenarios.Count; i++)
        {
            var s = scenarios[i];
            sb.AppendLine($"{i + 1}. **{s.Title}**"
                + (string.IsNullOrWhiteSpace(s.Screen) ? "" : $" — màn hình: {s.Screen}")
                + (s.RuleRefs.Count > 0 ? $" — kiểm rule: {string.Join(", ", s.RuleRefs)}" : "")
                // Mã AC đi kèm để Developer agent biết bước nào đang chứng minh câu nghiệm thu nào của
                // người dùng — bấm hỏng ở đây là hỏng đúng điều đã hứa, không phải một assertion nội bộ.
                + (s.AcRefs.Count > 0 ? $" — chứng minh câu nghiệm thu: {string.Join(", ", s.AcRefs)}" : ""));
            foreach (var step in s.Steps)
                sb.AppendLine($"   - {step}");
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

    private static UatScenarioSet ParseFallback(string? raw) =>
        LlmJson.TryDeserialize<UatScenarioSet>(raw) ?? new UatScenarioSet();

    // Chặn dữ liệu rác của model: bỏ kịch bản không tên/không bước, giới hạn số lượng.
    private static UatScenarioSet Sanitize(UatScenarioSet set)
    {
        set.Scenarios = set.Scenarios
            .Where(s => !string.IsNullOrWhiteSpace(s.Title) && s.Steps.Any(x => !string.IsNullOrWhiteSpace(x)))
            .Take(MaxScenarios)
            .ToList();
        foreach (var s in set.Scenarios)
        {
            s.Title = s.Title.Trim();
            s.Screen = (s.Screen ?? string.Empty).Trim();
            s.Steps = s.Steps.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
            s.RuleRefs = (s.RuleRefs ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
            // Mã AC được chuẩn hoá ngay khi nhận ("ac 2" → "AC-2") để mọi nơi đối chiếu sau này — vòng bổ
            // sung, trang review — không phải tự đoán lại cách model đã viết.
            s.AcRefs = (s.AcRefs ?? new List<string>())
                .Select(NormalizeRef)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return set;
    }
}
