using System.Text;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Tools;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Workflows;

/// <summary>Đầu vào của một lượt <see cref="PocOneShotBuilder.TryBuildAsync"/>.</summary>
/// <param name="TaskPrompt">Prompt bước POC đã dựng đủ (spec + UAT + quy ước + ghi chú hoãn) — y như prompt của đường agent.</param>
/// <param name="BuildPurpose">Nhãn Purpose ghi vào log cho lượt SINH.</param>
/// <param name="RepairPurpose">Nhãn Purpose ghi vào log cho các lượt SỬA.</param>
public sealed record PocOneShotRequest(
    Guid ProjectId,
    Agent Agent,
    AiModel Model,
    string TaskPrompt,
    Guid? WorkflowRunId,
    string BuildPurpose,
    string RepairPurpose,
    Action<string, string, string?>? OnProgress);

/// <summary>
/// Kết quả: <see cref="Built"/> = bản demo đã nằm trên đĩa và <see cref="Output"/> là bản bàn giao;
/// ngược lại <see cref="FallbackReason"/> nói vì sao caller phải dựng bằng đường agent nhiều bước. Khi
/// không dựng được, poc-demo.html CHƯA bị đụng tới.
/// </summary>
public sealed record PocOneShotOutcome(bool Built, string Output, string? FallbackReason)
{
    public static PocOneShotOutcome Fallback(string reason) => new(false, string.Empty, reason);
}

/// <summary>
/// Dựng bản POC bằng MỘT lượt sinh + tối đa <see cref="PocGenerationSettings.MaxRepairRounds"/> lượt sửa,
/// mỗi lượt là một lời gọi model ĐỘC LẬP — thay cho agent loop nhiều bước.
///
/// <para>
/// <b>Vì sao.</b> Ở đường agent, mỗi bước gửi lại TOÀN BỘ lịch sử (system prompt, spec, mọi đoạn HTML/JS đã
/// gửi qua tool ở các bước trước, mọi báo cáo audit), nên token input tăng theo bình phương số bước: một
/// POC ~10 màn hình đi 16–18 bước, prompt mỗi bước từ ~75k lên ~165k token, cộng lại ~1,6 triệu token —
/// trong khi phần model thật sự viết (vùng nội dung + vùng script) chỉ ~20k token. Ở đây lượt sinh tốn
/// một lần spec + một lần bản demo; mỗi lượt sửa tốn một lần spec + bản demo hiện tại + báo cáo, KHÔNG
/// mang lịch sử các lượt trước.
/// </para>
///
/// <para>
/// <b>Giữ nguyên các chốt chặn.</b> Ghi file đi qua chính các tool <c>SetPocContent</c>/<c>SetPocScript</c>/
/// <c>AppendPocContent</c> (cùng marker, cùng parser menu/vai, cùng tường thuật "đã dựng màn hình X"), và tự
/// kiểm là chính <c>AuditPocContent</c> — chỉ khác là CODE gọi nó sau mỗi lượt chứ không trông vào agent nhớ
/// gọi. System prompt của cả hai lượt là prompt bước POC nguyên văn, nên mọi luật nghiệp vụ/giao diện vẫn
/// có một nguồn duy nhất (<c>Developer/poc-preview.v1.md</c>); hai file <c>poc-oneshot</c>/<c>poc-repair</c>
/// chỉ đổi CÁCH GIAO. Prompt bước đứng làm system cũng để lượt sửa đọc lại được nó từ prompt cache của lượt sinh.
/// </para>
///
/// <para>
/// <b>Hai ưu điểm của đường cũ được giữ bằng cơ chế khác.</b> (1) Trần token output: câu trả lời phải kết
/// thúc bằng <c>===POC_END===</c>; thiếu ⇒ coi là bị cắt, KHÔNG ghi gì và trả <see cref="PocOneShotOutcome.Fallback"/>
/// để caller dựng bằng agent — đường đó sinh ra để chịu được trần này. Lời gọi lỗi/timeout cũng rơi về đó.
/// (2) Vòng tự kiểm → sửa vẫn còn, và lượt sửa nào làm TĂNG số mục còn lại thì bản tốt nhất được khôi phục.
/// </para>
/// </summary>
public class PocOneShotBuilder
{
    /// <summary>Purpose của lượt sinh trong pipeline chính.</summary>
    public const string BuildPurpose = "PocBuild";

    /// <summary>Purpose của các lượt sửa trong pipeline chính.</summary>
    public const string RepairPurpose = "PocRepair";

    private const string BuildPromptKey = "Developer/poc-oneshot.v1.md";
    private const string RepairPromptKey = "Developer/poc-repair.v1.md";

    // Số mục còn lại liệt kê trong bản bàn giao — đủ để người duyệt thấy loại lỗi, chi tiết đầy đủ đã có
    // ở panel "Máy đã tự kiểm" của trang POC Review (poc-verification.json).
    private const int MaxIssuesInHandoff = 10;

    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly WorkspaceTools _tools;
    private readonly PocGenerationSettings _settings;
    private readonly ILogger<PocOneShotBuilder> _logger;
    private Action<string, string, string?>? _progressSink;

    public PocOneShotBuilder(ILlmClient llm, PromptTemplateService prompts, WorkspaceTools tools,
        PocGenerationSettings settings, ILogger<PocOneShotBuilder> logger)
    {
        _llm = llm;
        _prompts = prompts;
        _tools = tools;
        _settings = settings;
        _logger = logger;
    }

    public bool Enabled => _settings.OneShot;

    /// <summary>
    /// Dựng bản demo vào <c>04_Implementation/poc-demo.html</c> của workspace mà <see cref="WorkspaceTools"/>
    /// (cùng scope) đang trỏ tới. Caller phải đã đặt workspace, seed template và nạp bối cảnh audit
    /// (<c>SetPocSpec</c>, UAT, dữ liệu mẫu thật…) — y như trước một lượt agent.
    /// </summary>
    public async Task<PocOneShotOutcome> TryBuildAsync(PocOneShotRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_tools.CurrentWorkspacePath))
            throw new InvalidOperationException("Workspace chưa được đặt cho lượt dựng POC.");

        _tools.SetRunCancellation(cancellationToken);
        _progressSink = request.OnProgress;
        _tools.SetProgressSink(request.OnProgress);
        var mockupPath = _tools.GetSafeFullPath(PocTemplate.MockupRelativePath);
        if (!File.Exists(mockupPath))
            return PocOneShotOutcome.Fallback($"không có {PocTemplate.MockupRelativePath} để ghi vào");

        // ---- Lượt SINH ----
        request.OnProgress?.Invoke("thinking", "Developer đang sinh toàn bộ POC trong một lượt (nội dung + logic nghiệp vụ)…", null);
        var build = await CallAsync(request, request.BuildPurpose, _prompts.Get(BuildPromptKey), cancellationToken);
        if (build.Error != null)
            return PocOneShotOutcome.Fallback(build.Error);

        var draft = PocOneShotReply.TryParseBuild(build.Content, out var parseError);
        if (draft == null)
            return PocOneShotOutcome.Fallback(parseError);

        var meta = draft.Meta;
        var setReply = await _tools.SetPocContent(draft.Content, meta.AppName, meta.Breadcrumb, meta.NavItems, meta.Roles);
        if (!setReply.StartsWith("POC content updated", StringComparison.Ordinal))
            return PocOneShotOutcome.Fallback(setReply);
        if (draft.Script.Length > 0)
            await _tools.SetPocScript(draft.Script);

        // Cảnh báo của SetPocContent (navItems/roles không đọc được) đi kèm lượt sửa đầu tiên: ở đường agent
        // model đọc chúng ngay trong kết quả tool, ở đây không ai đọc nếu không chuyển tiếp.
        var carryOver = ShellWarnings(setReply);

        // ---- Tự kiểm → sửa ----
        var notes = new List<string>();
        var rounds = 0;
        string? bestHtml = null;
        var bestIssues = int.MaxValue;
        PocVerificationSummary? verdict;
        while (true)
        {
            request.OnProgress?.Invoke("tool", rounds == 0
                ? "Tự kiểm bản POC vừa sinh (audit tĩnh + trình duyệt headless + kịch bản UAT)…"
                : $"Tự kiểm lại sau lượt sửa {rounds}…", null);
            var (report, summary) = await AuditAsync(cancellationToken);
            verdict = summary;
            if (verdict == null)
            {
                notes.Add("Tự kiểm không chạy được hoặc không ghi được kết quả (poc-verification.json) — dừng vòng sửa.");
                break;
            }

            var remaining = IssueCount(verdict);
            if (remaining < bestIssues)
            {
                bestIssues = remaining;
                bestHtml = await File.ReadAllTextAsync(mockupPath, cancellationToken);
            }

            if (remaining == 0 || rounds >= _settings.MaxRepairRounds)
                break;

            rounds++;
            request.OnProgress?.Invoke("thinking", $"Còn {remaining} mục cần sửa — lượt sửa {rounds}/{_settings.MaxRepairRounds}…", null);
            var html = await File.ReadAllTextAsync(mockupPath, cancellationToken);
            var repair = await CallAsync(request, request.RepairPurpose,
                BuildRepairMessage(html, report, carryOver), cancellationToken);
            if (repair.Error != null)
            {
                notes.Add($"Lượt sửa {rounds} không chạy được: {repair.Error}.");
                break;
            }

            var patch = PocOneShotReply.TryParseRepair(repair.Content, out var repairError);
            if (patch == null)
            {
                notes.Add($"Lượt sửa {rounds} bị bỏ: {repairError}.");
                break;
            }

            if (!string.IsNullOrWhiteSpace(patch.Notes))
                notes.Add($"Lượt sửa {rounds}: {patch.Notes}");
            if (patch.IsEmpty)
            {
                // Model không đề xuất gì — audit lại cũng ra đúng báo cáo cũ, đốt thêm một lượt là vô ích.
                notes.Add($"Lượt sửa {rounds} không đề xuất chỗ sửa nào.");
                break;
            }

            carryOver = await ApplyAsync(mockupPath, patch, cancellationToken);
        }

        // Lượt sửa cuối làm TĂNG số mục còn lại ⇒ trả lại bản tốt nhất, và kiểm lại để poc-verification.json
        // (thứ trang POC Review đọc) khớp với bản demo thật đang nằm trên đĩa.
        if (verdict != null && bestHtml != null && IssueCount(verdict) > bestIssues)
        {
            await File.WriteAllTextAsync(mockupPath, bestHtml, cancellationToken);
            notes.Add($"Lượt sửa cuối làm tăng số mục còn lại ({IssueCount(verdict)} > {bestIssues}) — đã giữ lại bản tốt nhất.");
            request.OnProgress?.Invoke("tool", "Khôi phục bản tốt nhất và tự kiểm lại…", null);
            (_, verdict) = await AuditAsync(cancellationToken);
        }

        return new PocOneShotOutcome(true, BuildHandoff(draft.Handoff, rounds, verdict, notes), null);
    }

    // ---- nội bộ ----

    private async Task<(string Content, string? Error)> CallAsync(PocOneShotRequest request, string purpose,
        string userMessage, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, request.TaskPrompt),
            new(ChatRole.User, userMessage)
        };

        try
        {
            var result = await _llm.ChatWithLogAsync(request.Model, messages,
                new ModelCallLogContext(request.ProjectId, request.Agent, purpose, request.WorkflowRunId),
                cancellationToken: cancellationToken);
            return result.IsSuccess
                ? (result.Content, null)
                : (string.Empty, $"lời gọi model lỗi: {result.ErrorMessage}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Budget guard chặn, endpoint hỏng… — không để nó thành lỗi của cả bước: đường agent còn đó.
            _logger.LogWarning(ex, "Lời gọi {Purpose} dựng POC thất bại.", purpose);
            return (string.Empty, $"lời gọi model lỗi: {ex.Message}");
        }
    }

    private async Task<(string Report, PocVerificationSummary? Summary)> AuditAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow.AddSeconds(-1);
        string report;
        try
        {
            report = await _tools.AuditPocContent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tầng tự kiểm hỏng (vd môi trường thiếu thứ nó cần) không được biến một bản demo đã dựng xong
            // thành bước thất bại — cùng tinh thần fail-open của chính các tầng bên trong audit.
            _logger.LogWarning(ex, "AuditPocContent lỗi trong lượt dựng POC một lần.");
            return (string.Empty, null);
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Chỉ tin bản ghi của CHÍNH lượt audit này: lưu kết quả là fail-open, nên một file cũ hơn có thể vẫn
        // nằm đó và báo "sạch" cho một bản demo chưa từng được kiểm.
        var summary = PocVerification.TryLoad(_tools.CurrentWorkspacePath);
        return (report, summary != null && summary.CheckedAtUtc >= startedAt ? summary : null);
    }

    private static int IssueCount(PocVerificationSummary summary) => summary.OpenIssues.Count + summary.Regressions.Count;

    private string BuildRepairMessage(string html, string report, IReadOnlyList<string> carryOver)
    {
        var sb = new StringBuilder(_prompts.Get(RepairPromptKey).TrimEnd());
        sb.AppendLine().AppendLine();
        sb.AppendLine("# ĐẦU VÀO: Vùng nội dung hiện tại (POC_CONTENT)");
        sb.AppendLine("```html");
        sb.AppendLine(PocTemplate.GetContentBody(html).Trim('\n', '\r'));
        sb.AppendLine("```").AppendLine();
        sb.AppendLine("# ĐẦU VÀO: Vùng script hiện tại (POC_SCRIPT)");
        sb.AppendLine("```js");
        sb.AppendLine(PocTemplate.GetScriptBody(html).Trim('\n', '\r'));
        sb.AppendLine("```").AppendLine();
        sb.AppendLine("# ĐẦU VÀO: Báo cáo AuditPocContent");
        sb.AppendLine(report.Trim());
        if (carryOver.Count > 0)
        {
            sb.AppendLine().AppendLine("# ĐẦU VÀO: Những gì lượt trước KHÔNG áp được (sửa lại cho đúng)");
            foreach (var line in carryOver)
                sb.AppendLine("- " + line);
        }
        return sb.ToString();
    }

    // Thứ tự áp: các chỗ sửa trước (chúng được viết theo đúng văn bản model vừa xem), rồi menu/vai (dựng
    // lại shell trên vùng nội dung ĐÃ sửa), rồi phần nối thêm, cuối cùng là script viết lại.
    private async Task<IReadOnlyList<string>> ApplyAsync(string mockupPath, PocRepairPatch patch, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        if (patch.Edits.Count > 0)
        {
            var html = await File.ReadAllTextAsync(mockupPath, cancellationToken);
            var (regions, editFailures) = PocOneShotReply.ApplyEdits(
                [PocTemplate.GetContentBody(html), PocTemplate.GetScriptBody(html)], patch.Edits);
            failures.AddRange(editFailures);

            var updated = regions[0] == PocTemplate.GetContentBody(html)
                ? html
                : PocTemplate.ReplaceContent(html, regions[0].Trim()) ?? html;
            if (regions[1] != PocTemplate.GetScriptBody(html))
                updated = PocTemplate.ReplaceScript(updated, regions[1]) ?? updated;
            await File.WriteAllTextAsync(mockupPath, updated, cancellationToken);
        }

        if (patch.Meta is { } meta)
        {
            // SetPocContent là đường DUY NHẤT dựng lại menu/vai, và nó ghi đè cả vùng nội dung — nên gửi lại
            // chính vùng nội dung hiện tại. Tắt tạm tường thuật: đây không phải lúc "dựng màn hình 1/N" lần nữa.
            var html = await File.ReadAllTextAsync(mockupPath, cancellationToken);
            var sink = _progressSink;
            _tools.SetProgressSink(null);
            var reply = await _tools.SetPocContent(PocTemplate.GetContentBody(html).Trim(), meta.AppName, meta.Breadcrumb, meta.NavItems, meta.Roles);
            _tools.SetProgressSink(sink);
            failures.AddRange(ShellWarnings(reply));
        }

        if (!string.IsNullOrWhiteSpace(patch.AppendContent))
            await _tools.AppendPocContent(patch.AppendContent);

        if (!string.IsNullOrWhiteSpace(patch.Script))
            await _tools.SetPocScript(patch.Script);

        return failures;
    }

    // Phần "WARNING: …" trong câu trả lời của SetPocContent (menu/vai không đọc được) — xem WorkspaceTools.
    private static IReadOnlyList<string> ShellWarnings(string toolReply) =>
        toolReply.Split(" WARNING: ", StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(w => "WARNING: " + w.Trim())
            .ToList();

    private static string BuildHandoff(string modelHandoff, int rounds, PocVerificationSummary? verdict, IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(modelHandoff))
            sb.AppendLine(modelHandoff.Trim()).AppendLine();

        sb.AppendLine("---");
        sb.AppendLine(rounds == 0
            ? "Cách dựng: sinh trọn bản demo trong một lượt, tự kiểm một lần."
            : $"Cách dựng: sinh trọn bản demo trong một lượt + {rounds} lượt sửa theo tự kiểm.");

        if (verdict == null)
        {
            sb.AppendLine("Tự kiểm cuối: không có kết quả.");
        }
        else if (IssueCount(verdict) == 0)
        {
            sb.AppendLine($"Tự kiểm cuối: sạch — {verdict.CoveredScreens}/{verdict.SpecScreens} màn hình của spec, "
                + $"{verdict.SelfTestResults.Count} rule self-test, UAT {verdict.UatCovered}/{verdict.UatTotal}.");
        }
        else
        {
            var all = verdict.Regressions.Select(r => "[Hồi quy] " + r).Concat(verdict.OpenIssues).ToList();
            sb.AppendLine($"Tự kiểm cuối: còn {all.Count} mục chưa đạt:");
            foreach (var issue in all.Take(MaxIssuesInHandoff))
                sb.AppendLine("- " + issue);
            if (all.Count > MaxIssuesInHandoff)
                sb.AppendLine($"- … và {all.Count - MaxIssuesInHandoff} mục khác (xem panel \"Máy đã tự kiểm\" ở trang POC Review).");
        }

        foreach (var note in notes)
            sb.AppendLine(note);
        return sb.ToString().TrimEnd();
    }
}
