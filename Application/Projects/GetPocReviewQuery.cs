using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>Một vòng "Yêu cầu chỉnh sửa" POC đã hoàn tất: bàn giao cuối của agent chính là changelog đối chiếu từng ghi chú.</summary>
public record PocRevisionEntry(string Title, DateTime? FinishedAt, string Output);

/// <summary>Một quy tắc nghiệp vụ của spec + các kịch bản UAT kiểm nó (cross-link BR-n ↔ RuleRefs) — truy vết yêu cầu↔POC.</summary>
public record PocRuleCoverage(string Rule, IReadOnlyList<string> ScenarioTitles);

/// <summary>
/// "Yêu cầu POC này bao phủ" — chắt từ AI Design Spec đã duyệt để người review thấy POC ĐÁNG LẼ phủ gì
/// (màn hình, quy tắc nghiệp vụ, ví dụ tính thử đã chốt) và quy tắc nào có kịch bản UAT kiểm — không chỉ
/// đi tìm lỗi mà còn biết độ phủ so với yêu cầu.
/// </summary>
public record PocReviewCoverage(
    IReadOnlyList<string> Screens,
    IReadOnlyList<PocRuleCoverage> Rules,
    IReadOnlyList<string> WorkedExamples);

public record PocReviewPage(
    Guid ProjectId,
    string ProjectName,
    bool HasMockup,
    UatScenarioSet Scenarios,
    IReadOnlyList<PocRevisionEntry> Revisions,
    PocReviewCoverage Coverage,
    PocVerificationSummary? Verification);

/// <summary>
/// Dữ liệu cho trang review POC (Projects/PocReview): tên project, POC đã tồn tại chưa, bộ kịch bản
/// UAT (checklist đi từng bước, sinh sau khi POC dựng xong — xem <see cref="UatScenarioService"/>) và
/// changelog các vòng chỉnh sửa đã chạy (bàn giao của agent, để người review thấy NHỮNG GÌ ĐÃ ĐỔI sau
/// khi gửi ghi chú thay vì phải tự so hai bản POC).
/// </summary>
public class GetPocReviewQuery
{
    private const int MaxRevisionEntries = 5;

    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;
    private readonly UatScenarioService _uatScenarios;
    private readonly IProjectArtifactCatalog _artifactCatalog;

    public GetPocReviewQuery(AppDbContext db, WorkspacePathResolver workspacePathResolver, UatScenarioService uatScenarios, IProjectArtifactCatalog artifactCatalog)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
        _uatScenarios = uatScenarios;
        _artifactCatalog = artifactCatalog;
    }

    public async Task<PocReviewPage?> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.AsNoTracking()
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return null;

        var workspaceFolder = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
        var mockupPath = _workspacePathResolver.GetMockupPath(workspaceFolder);

        // Kết quả vòng tự kiểm CUỐI của AuditPocContent (nếu có) — panel "Máy đã tự kiểm" trên trang
        // review. Fail-open: chưa có file (POC cũ) ⇒ null, view tự ẩn panel.
        var verification = PocVerification.TryLoad(_workspacePathResolver.GetProjectWorkspacePath(workspaceFolder));

        var scenarios = await _uatScenarios.LoadAsync(project.Id, project.Name, cancellationToken);

        // Truy vết yêu cầu↔POC (U2): parse AI Design Spec (bản duyệt mới nhất) thành checklist — cùng
        // PocSpec mà audit dùng — để trang review nêu POC ĐÁNG LẼ phủ gì và quy tắc nào có kịch bản kiểm.
        var coverage = BuildCoverage(project, scenarios);

        // Các vòng chỉnh sửa POC đã xong, mới nhất trước. Prompt revision yêu cầu bàn giao cuối nêu rõ
        // đã đổi gì ứng với từng ghi chú — hiển thị nguyên văn làm changelog.
        var revisions = await _db.AgentTasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId
                        && t.Type == AgentTaskType.PocPreview
                        && t.RevisionFeedback != null
                        && t.Status == AgentTaskStatus.Completed
                        && t.Output != null && t.Output != "")
            .OrderByDescending(t => t.FinishedAt ?? t.CreatedAt)
            .Take(MaxRevisionEntries)
            .Select(t => new PocRevisionEntry(t.Title, t.FinishedAt, t.Output!))
            .ToListAsync(cancellationToken);

        return new PocReviewPage(project.Id, project.Name, File.Exists(mockupPath), scenarios, revisions, coverage, verification);
    }

    // Nạp AI Design Spec mới nhất (mọi phiên bản), parse bằng chính PocSpec của audit, rồi cross-link mỗi
    // BR-n với các kịch bản UAT có RuleRefs chứa nó. Không có spec/parse rỗng ⇒ coverage rỗng (view tự ẩn
    // panel) — fail-open, không đổi hành vi cũ.
    private PocReviewCoverage BuildCoverage(Domain.Project project, UatScenarioSet scenarios)
    {
        var specContent = project.Documents
            .Where(d => d.FileName == _artifactCatalog.AiDesignSpec.FileName)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => d.Content)
            .FirstOrDefault();

        var spec = PocSpec.Parse(specContent);
        if (spec.Screens.Count == 0 && spec.Rules.Count == 0 && spec.WorkedExamples.Count == 0)
            return new PocReviewCoverage(Array.Empty<string>(), Array.Empty<PocRuleCoverage>(), Array.Empty<string>());

        var rules = spec.Rules.Select(rule =>
        {
            var refTag = RuleRefTag(rule);
            var titles = string.IsNullOrEmpty(refTag)
                ? Array.Empty<string>()
                : scenarios.Scenarios
                    .Where(s => s.RuleRefs.Any(r => string.Equals(r, refTag, StringComparison.OrdinalIgnoreCase)))
                    .Select(s => s.Title)
                    .Distinct()
                    .ToArray();
            return new PocRuleCoverage(rule, titles);
        }).ToList();

        var worked = spec.WorkedExamples
            .Select(w => $"{w.Ref}{(string.IsNullOrWhiteSpace(w.RuleRef) ? "" : $" ({w.RuleRef})")}: {w.Description} ⇒ {w.Expected}")
            .ToList();

        return new PocReviewCoverage(spec.Screens, rules, worked);
    }

    // "BR-3: đơn đã duyệt thì khóa sửa" → "BR-3" (để cross-link với UatScenario.RuleRefs). Không khớp ⇒ "".
    private static string RuleRefTag(string rule)
    {
        var m = System.Text.RegularExpressions.Regex.Match(rule ?? string.Empty, @"^\s*(BR-\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }
}
