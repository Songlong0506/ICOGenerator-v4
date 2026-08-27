using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Giữ các góp ý GIAO DIỆN đã được chấp nhận sống sót qua một vòng dựng lại POC.
///
/// Bài toán: đường "Nhờ đội Dev chỉnh bản demo" (<see cref="Application.Requirements.DispatchPocFeedbackUseCase"/>)
/// chạy một task <c>PocPreview</c> có <c>RevisionFeedback</c>, và agent vá thẳng vào
/// <c>04_Implementation/poc-demo.html</c> — kết quả CHỈ nằm trong HTML, không đụng Brief/Spec. Nhưng khi
/// tài liệu được sửa (đường "Gửi về Requirement") và người dùng duyệt lại, một WorkflowRun MỚI dựng POC
/// từ đầu: <c>AgentTaskWorker.EnsureDesignAssetsAsync</c> ghi đè cả <c>poc-demo.html</c> về shell
/// template. Không có gì chở các góp ý giao diện ấy sang bản mới — chúng mất trắng, và người review gặp
/// lại đúng những lỗi họ đã góp ý một lần rồi.
///
/// Cách xử lý: sau MỖI vòng chỉnh sửa POC, chắt lọc các ghi chú vừa được sửa thành QUY ƯỚC TRÌNH BÀY
/// dùng lại được, lưu ở <c>04_Implementation/poc-ui-conventions.json</c> — nằm NGOÀI file bị sinh lại,
/// nên nó sống sót — rồi nối vào prompt dựng POC ở MỌI vòng (xem <see cref="BuildPromptBlock"/>).
///
/// Khác <see cref="PocFeedbackMemoryService"/>: bộ nhớ kia bồi bài học vào checklist phỏng vấn của BA cho
/// các dự án SAU; bộ này giữ quy ước cho CHÍNH dự án này. FAIL-OPEN toàn phần như mọi tầng bộ nhớ khác:
/// lỗi ⇒ giữ nguyên bộ cũ, vòng sau gộp bù.
/// </summary>
public class PocUiConventionService
{
    public const string FileName = "poc-ui-conventions.json";

    /// <summary>
    /// Trần số quy ước. Bộ này đi vào prompt của MỌI vòng dựng POC, nên để nó phình vô hạn là lấy dần chỗ
    /// của chính AI Design Spec trong cùng cửa sổ ngữ cảnh. Chạm trần thì giữ các quy ước MỚI NHẤT.
    /// </summary>
    private const int MaxConventions = 24;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly WorkspacePathResolver _workspacePathResolver;
    private readonly BAAgentResolver _agentResolver;
    private readonly ILogger<PocUiConventionService> _logger;

    public PocUiConventionService(
        AppDbContext db,
        ILlmClient llm,
        PromptTemplateService prompts,
        WorkspacePathResolver workspacePathResolver,
        BAAgentResolver agentResolver,
        ILogger<PocUiConventionService> logger)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _workspacePathResolver = workspacePathResolver;
        _agentResolver = agentResolver;
        _logger = logger;
    }

    /// <summary>
    /// Chắt lọc các ghi chú vừa đi qua một vòng chỉnh sửa POC thành quy ước trình bày của dự án.
    ///
    /// Phải gọi TRƯỚC <c>MarkSentPocCommentsAddressedAsync</c>: nó đọc đúng tập ghi chú ở trạng thái
    /// <see cref="PocCommentStatus.Sent"/>, mà lời gọi kia đóng hết tập đó lại thành <c>Addressed</c>.
    /// Mọi lỗi đều nuốt + log — đây là bước phụ trợ, không được làm fail một task POC đã chạy xong.
    /// </summary>
    public async Task TryHarvestAsync(Guid projectId, Guid? workflowRunId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await _db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
            if (project == null)
                return;

            // Chỉ ghi chú ĐÃ ĐƯỢC GỬI cho Developer — chúng đã thật sự dẫn tới một lần sửa bản demo, tức
            // là người dùng đã chấp nhận cách trình bày mới. Ghi chú còn Open chưa ai đồng ý gì cả.
            var comments = await _db.PocComments.AsNoTracking()
                .Where(c => c.ProjectId == projectId && c.Status == PocCommentStatus.Sent)
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .ToListAsync(cancellationToken);
            if (comments.Count == 0)
                return;

            var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
            if (ba == null)
                return;

            var existing = await LoadAsync(project.Id, project.Name, cancellationToken);
            var harvested = await DistillAsync(existing, comments, ba, ba.AiModel!, projectId, workflowRunId, cancellationToken);
            if (harvested == null)
                return; // fail-open: giữ nguyên bộ quy ước cũ, vòng sau gộp bù.

            var merged = Merge(harvested, existing);

            // Model được yêu cầu xuất lại TOÀN BỘ bộ đã gộp, nên một kết quả NGHÈO HƠN bộ đang có nghĩa là
            // nó vừa đánh rơi quy ước cũ chứ không phải người dùng đã đổi ý. Nhận vào là làm bản demo lùi
            // lại — cùng lý do vòng bổ sung UAT chỉ nhận bộ phủ nhiều hơn.
            if (merged.Conventions.Count < existing.Conventions.Count)
            {
                _logger.LogWarning(
                    "POC UI convention harvest for project {ProjectId} returned {New} conventions, fewer than the {Existing} already stored — keeping the stored set.",
                    projectId, merged.Conventions.Count, existing.Conventions.Count);
                return;
            }

            if (merged.Conventions.Count == 0)
                return; // không rút được gì và cũng chưa có gì: đừng tạo một file rỗng.

            var path = GetConventionPath(project.Id, project.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(merged, WriteOptions), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown thật sự thì để caller xử lý như mọi bước khác.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not harvest POC UI conventions for project {ProjectId}.", projectId);
        }
    }

    /// <summary>Đọc bộ quy ước đã lưu của project; không có/hỏng ⇒ bộ rỗng (prompt POC y như trước).</summary>
    public async Task<PocUiConventionSet> LoadAsync(Guid projectId, string projectName, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = GetConventionPath(projectId, projectName);
            if (!File.Exists(path))
                return new PocUiConventionSet();

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return Sanitize(JsonSerializer.Deserialize<PocUiConventionSet>(json, LlmJson.Options) ?? new PocUiConventionSet());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read POC UI conventions for project {ProjectId}.", projectId);
            return new PocUiConventionSet();
        }
    }

    /// <summary>
    /// Khối văn bản NỐI vào prompt dựng POC. Đây là toàn bộ lý do bộ quy ước tồn tại: bản demo mang các
    /// thay đổi ấy đã bị ghi đè, nên chỗ duy nhất còn nói được với agent là prompt. Bộ rỗng ⇒ chuỗi rỗng
    /// (prompt như cũ, không có tác dụng phụ lên dự án chưa từng đi đường "chỉnh bản demo").
    /// </summary>
    public static string BuildPromptBlock(PocUiConventionSet? set)
    {
        var conventions = set?.Conventions ?? new List<PocUiConvention>();
        if (conventions.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("# QUY ƯỚC TRÌNH BÀY ĐÃ CHỐT — BẮT BUỘC ÁP DỤNG");
        sb.AppendLine();
        sb.AppendLine("Đây là các góp ý về GIAO DIỆN mà người dùng đã ghim trên bản demo ở những vòng TRƯỚC và đội Dev đã sửa theo. Bản demo đó vừa bị dựng lại từ đầu, nên nếu bạn không áp dụng lại thì người dùng mở lên sẽ gặp đúng những lỗi họ đã góp ý một lần rồi.");
        sb.AppendLine("- Áp dụng MỌI quy ước dưới đây cho phần UI tương ứng.");
        sb.AppendLine("- CHỈ áp dụng khi màn hình/phần tử tương ứng CÒN trong AI Design Spec của vòng này. Spec đã bỏ màn hình đó thì bỏ luôn quy ước — TUYỆT ĐỐI không dựng thêm màn hình chỉ để có chỗ áp dụng.");
        sb.AppendLine("- AI Design Spec luôn THẮNG khi mâu thuẫn: các quy ước này nói về cách TRÌNH BÀY, chúng không thêm/bớt/đổi nghiệp vụ.");
        sb.AppendLine();

        foreach (var c in conventions)
        {
            sb.AppendLine($"- **{c.Id}**"
                + (string.IsNullOrWhiteSpace(c.Screen) ? "" : $" — màn hình: {c.Screen}")
                + $" — {c.Text}");
        }

        return sb.ToString();
    }

    // Rút quy ước từ các ghi chú vừa được sửa. Trả null khi lời gọi lỗi/không đọc nổi để caller fail-open
    // (giữ nguyên file cũ); bộ RỖNG nghĩa là "không có gì đáng giữ" — vẫn là một kết quả hợp lệ.
    private async Task<PocUiConventionSet?> DistillAsync(
        PocUiConventionSet existing,
        List<PocComment> comments,
        Agent ba,
        AiModel model,
        Guid projectId,
        Guid? workflowRunId,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        if (existing.Conventions.Count > 0)
        {
            sb.AppendLine("## Bộ quy ước trình bày đã chốt của dự án này");
            foreach (var c in existing.Conventions)
                sb.AppendLine($"- {c.Text}" + (string.IsNullOrWhiteSpace(c.Screen) ? "" : $" [màn hình \"{c.Screen}\"]"));
            sb.AppendLine();
        }

        sb.AppendLine("## Ghi chú người dùng ghim trên bản demo và đội Dev vừa sửa xong theo");
        foreach (var c in comments)
        {
            sb.Append("- ");
            if (!string.IsNullOrWhiteSpace(c.PageView))
                sb.Append($"[Màn hình \"{c.PageView}\"] ");
            if (!string.IsNullOrWhiteSpace(c.ElementLabel))
                sb.Append($"Phần tử: {c.ElementLabel} — ");
            sb.AppendLine(c.Comment.Trim());
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/poc-ui-convention.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var (result, structured) = await _llm.ChatStructuredAsync<PocUiConventionSet>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAPocUiConvention", workflowRunId),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogWarning("POC UI convention harvest failed for project {ProjectId}: {Error}", projectId, result.ErrorMessage ?? result.Content);
            return null;
        }

        var harvested = structured ?? LlmJson.TryDeserialize<PocUiConventionSet>(result.Content, requireKnownProperty: true);
        if (harvested != null)
            return harvested;

        // Gọi được nhưng phản hồi không đọc nổi: giữ nguyên bộ cũ. Khác PocFeedbackMemoryService (nó dời
        // con trỏ để không gộp lại) — ở đây không có con trỏ, ghi chú vẫn sẽ chuyển Addressed ngay sau
        // lời gọi này, nên mất là mất hẳn; nhưng ghi một bộ rỗng đè lên bộ cũ còn tệ hơn.
        _logger.LogWarning("POC UI convention harvest for project {ProjectId} returned unparseable output.", projectId);
        return null;
    }

    // Bộ do model xuất ra là bộ ĐÃ GỘP (prompt yêu cầu xuất lại toàn bộ). Việc còn lại của C#: giữ mốc
    // thời gian của các quy ước cũ (khớp theo nội dung) để trần MaxConventions cắt đúng cái cũ nhất, chứ
    // không để model tự đặt lại ngày rồi mọi thứ cùng "mới".
    private static PocUiConventionSet Merge(PocUiConventionSet harvested, PocUiConventionSet existing)
    {
        var capturedBefore = existing.Conventions
            .GroupBy(c => Normalize(c.Text), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(c => c.CapturedAtUtc), StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        foreach (var c in harvested.Conventions)
        {
            c.CapturedAtUtc = capturedBefore.TryGetValue(Normalize(c.Text ?? string.Empty), out var before) ? before : now;
        }

        return Sanitize(harvested);
    }

    // Chặn dữ liệu rác của model: bỏ quy ước rỗng, bỏ trùng theo nội dung, cắt về trần (giữ MỚI NHẤT) rồi
    // đánh lại mã UI-n. Mã do C# đánh chứ không nhận của model: nó phải ổn định theo thứ tự lưu để hai lần
    // đọc cùng một file cho cùng một mã.
    private static PocUiConventionSet Sanitize(PocUiConventionSet set)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<PocUiConvention>();

        foreach (var c in set.Conventions ?? new List<PocUiConvention>())
        {
            var text = (c.Text ?? string.Empty).Trim();
            if (text.Length == 0 || !seen.Add(Normalize(text)))
                continue;

            c.Text = text;
            c.Screen = (c.Screen ?? string.Empty).Trim();
            c.SourceComment = (c.SourceComment ?? string.Empty).Trim();
            kept.Add(c);
        }

        // Cắt theo mốc thời gian nhưng GIỮ thứ tự gốc của phần còn lại: bộ quy ước được đọc như một danh
        // sách, đảo thứ tự mỗi lần lưu chỉ làm diff của file nhiễu mà không nói thêm điều gì.
        if (kept.Count > MaxConventions)
        {
            var cutoff = kept.OrderByDescending(c => c.CapturedAtUtc).Take(MaxConventions).ToHashSet();
            kept = kept.Where(cutoff.Contains).ToList();
        }

        for (var i = 0; i < kept.Count; i++)
            kept[i].Id = $"UI-{i + 1}";

        set.Conventions = kept;
        return set;
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private string GetConventionPath(Guid projectId, string projectName)
    {
        var mockupPath = _workspacePathResolver.GetMockupPath(WorkspacePathResolver.GetWorkspaceFolder(projectId, projectName));
        return Path.Combine(Path.GetDirectoryName(mockupPath)!, FileName);
    }
}
