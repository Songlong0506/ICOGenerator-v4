using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>Một quy tắc nghiệp vụ của spec + các kịch bản UAT kiểm nó (cross-link BR-n ↔ RuleRefs) — truy vết yêu cầu↔POC.</summary>
public record PocRuleCoverage(string Rule, IReadOnlyList<string> ScenarioTitles);

/// <summary>
/// Một câu nghiệm thu người dùng đã duyệt (AC-n, § 14 của spec — nguyên văn dòng "Hoàn thành khi: …" của
/// Product Brief) + các kịch bản UAT chứng minh nó. Đây là dòng truy vết ĐẦY ĐỦ nhất trên trang review:
/// người nghiệm thu đọc lại đúng câu mình đã duyệt và thấy ngay có kịch bản nào chứng minh hay không.
/// </summary>
public record PocAcceptanceCoverage(string Ref, string? Feature, string Text, IReadOnlyList<string> ScenarioTitles);

/// <summary>
/// "Yêu cầu POC này bao phủ" — chắt từ AI Design Spec đã duyệt để người review thấy POC ĐÁNG LẼ phủ gì
/// (màn hình, quy tắc nghiệp vụ, ví dụ tính thử đã chốt) và quy tắc nào có kịch bản UAT kiểm — không chỉ
/// đi tìm lỗi mà còn biết độ phủ so với yêu cầu.
/// </summary>
public record PocReviewCoverage(
    IReadOnlyList<string> Screens,
    IReadOnlyList<PocRuleCoverage> Rules,
    IReadOnlyList<string> WorkedExamples,
    IReadOnlyList<PocAcceptanceCoverage> AcceptanceCriteria);

/// <summary>
/// Các vòng dựng POC đã được chụp lại + khác biệt màn hình giữa bản HIỆN TẠI và vòng liền trước.
/// Đối trọng bằng BẰNG CHỨNG cho phần "đã sửa được gì" (vốn suy từ kết quả tự kiểm) và cho bản bàn giao
/// bằng chữ của agent: người nghiệm thu mở lại đúng bản mình đã xem ở vòng trước để tự đối chiếu.
/// </summary>
public record PocVersionHistory(
    IReadOnlyList<PocSnapshot> Snapshots,
    PocSnapshotDiff DiffWithPrevious,
    int? PreviousVersion);

public record PocReviewPage(
    Guid ProjectId,
    string ProjectName,
    bool HasMockup,
    UatScenarioSet Scenarios,
    // LỊCH SỬ ghi chú của MỌI phiên bản Product Brief (ghi chú Brief + ghi chú POC + các vòng Dev chỉnh
    // demo), gom theo version — thay cho panel "Nhật ký vòng sửa" cũ vốn chỉ có bàn giao của agent.
    IReadOnlyList<PocNoteHistoryVersion> History,
    PocReviewCoverage Coverage,
    PocVerificationSummary? Verification,
    // Những gì vòng kiểm này SỬA ĐƯỢC so với vòng trước (rule/kịch bản FAIL→PASS, số điểm chưa đạt giảm).
    // Đối trọng của Verification.Regressions: người review vòng thứ hai trở đi cần biết lần sửa vừa rồi
    // đụng vào những gì để nhìn đúng chỗ, thay vì rà lại cả bản demo từ đầu.
    IReadOnlyList<string> VerificationFixes,
    // Lịch sử các vòng dựng POC (bản chụp mở lại được) + màn hình thêm/bỏ so với vòng liền trước.
    PocVersionHistory Versions,
    // Cổng POC đang mở (có run chờ duyệt ĐÚNG ở bước PocPreview) ⇒ trang này còn gửi được yêu cầu chỉnh
    // bản demo cho Developer; đã đi qua bước POC thì nút đó vô nghĩa và bị ẩn.
    bool PocGateOpen,
    // Số vòng chỉnh sửa POC đã dùng / trần cho phép — hiện thẳng trên nút để người dùng biết còn mấy lượt
    // trước khi hết (trần là DeliveryPipeline.MaxRevisionRounds).
    int PocRevisionsUsed,
    int PocRevisionLimit,
    // Nghiệm thu của người yêu cầu: null = chưa ai xác nhận bản demo đạt. Trang review đổi khối hành động
    // cuối thành một dòng "đã nghiệm thu bởi X lúc Y" khi có giá trị.
    DateTime? PocAcceptedAtUtc,
    string? PocAcceptedBy,
    // Bản Product Brief mà POC đang phục vụ được dựng từ đó — ghi chú ghim mới đóng dấu bản này, và
    // danh sách dùng nó để gắn nhãn cho ghi chú của các thế hệ TRƯỚC.
    string BriefVersion,
    // Chặng của dự án (suy ra, không lưu DB — xem ProjectStatusResolver). Trang này hiện nó thành badge
    // ở đầu trang: khi đã "POC Approve" thì badge là lời giải thích cho việc ghi chú đang khoá.
    ProjectStatusRow Status);

/// <summary>
/// Dữ liệu cho trang review POC (Projects/PocReview): tên project, POC đã tồn tại chưa, bộ kịch bản
/// UAT (checklist đi từng bước, sinh sau khi POC dựng xong — xem <see cref="UatScenarioService"/>) và
/// changelog các vòng chỉnh sửa đã chạy (bàn giao của agent, để người review thấy NHỮNG GÌ ĐÃ ĐỔI sau
/// khi gửi ghi chú thay vì phải tự so hai bản POC).
/// </summary>
public class GetPocReviewQuery
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;
    private readonly UatScenarioService _uatScenarios;
    private readonly IProjectArtifactCatalog _artifactCatalog;
    private readonly GetPocNoteHistoryQuery _history;
    private readonly ProjectStatusResolver _statuses;

    public GetPocReviewQuery(AppDbContext db, WorkspacePathResolver workspacePathResolver, UatScenarioService uatScenarios, IProjectArtifactCatalog artifactCatalog, GetPocNoteHistoryQuery history, ProjectStatusResolver statuses)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
        _uatScenarios = uatScenarios;
        _artifactCatalog = artifactCatalog;
        _history = history;
        _statuses = statuses;
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

        // So với vòng kiểm LIỀN TRƯỚC để nêu "đã sửa được gì" — cùng nguồn lịch sử mà tầng hồi quy dùng.
        var workspacePath = _workspacePathResolver.GetProjectWorkspacePath(workspaceFolder);
        var previousVerification = PocVerification.TryLoadHistory(workspacePath).LastOrDefault();
        var verificationFixes = verification == null
            ? new List<string>()
            : PocVerification.DetectFixes(previousVerification, verification);

        // Các vòng dựng đã chụp + diff màn hình giữa bản đang phục vụ và vòng liền TRƯỚC. Bản chụp mới
        // nhất chính là bản hiện tại (chụp ngay khi vòng đó xong), nên "vòng trước" là bản áp chót.
        var versions = BuildVersionHistory(workspacePath, mockupPath);

        var scenarios = await _uatScenarios.LoadAsync(project.Id, project.Name, cancellationToken);

        // Truy vết yêu cầu↔POC (U2): parse AI Design Spec (bản duyệt mới nhất) thành checklist — cùng
        // PocSpec mà audit dùng — để trang review nêu POC ĐÁNG LẼ phủ gì và quy tắc nào có kịch bản kiểm.
        var coverage = BuildCoverage(project, scenarios);

        // Lịch sử ghi chú của MỌI phiên bản Brief — gồm cả các vòng chỉnh sửa (bàn giao của agent giờ là
        // cột "đã xử lý" của chính dòng vòng sửa đó) nên panel changelog riêng không còn lý do tồn tại.
        var history = await _history.ExecuteAsync(projectId, cancellationToken);

        // Cổng POC còn mở không, và đã dùng mấy vòng chỉnh sửa — đếm CÙNG cách với
        // RequestStageRevisionUseCase (task PocPreview có RevisionFeedback) để con số trên nút không vênh
        // với cổng thật; đếm trên toàn project vì mỗi lần Approve là một delivery run mới cho cùng POC.
        var pocGateOpen = await _db.WorkflowRuns.AsNoTracking()
            .AnyAsync(r => r.ProjectId == projectId
                           && r.Status == WorkflowRunStatus.WaitingForHuman
                           && r.CurrentStage == WorkflowStageKey.PocPreview, cancellationToken);

        var revisionsUsed = await _db.AgentTasks.AsNoTracking()
            .CountAsync(t => t.ProjectId == projectId
                             && t.Type == AgentTaskType.PocPreview
                             && t.RevisionFeedback != null, cancellationToken);

        var briefVersion = BriefVersionResolver.Highest(project.Documents
            .Where(d => d.IsApproved && d.FileName == _artifactCatalog.ProductBrief.FileName)
            .Select(d => d.VersionName));

        var status = await _statuses.ResolveAsync(projectId, cancellationToken)
                     ?? new ProjectStatusRow(projectId, ProjectStatus.New, false);

        return new PocReviewPage(
            project.Id, project.Name, File.Exists(mockupPath), scenarios, history, coverage, verification, verificationFixes,
            versions, pocGateOpen, revisionsUsed, DeliveryPipeline.MaxRevisionRounds,
            project.PocAcceptedAtUtc, project.PocAcceptedBy, briefVersion, status);
    }

    // Bản chụp của MỖI vòng dựng (xem PocSnapshots) + diff màn hình giữa bản đang phục vụ và vòng liền
    // trước. Chưa có bản chụp nào (POC dựng trước khi có tính năng này, hoặc mới đúng một vòng) ⇒ lịch sử
    // rỗng và view tự ẩn panel — fail-open, không đổi hành vi cũ.
    private static PocVersionHistory BuildVersionHistory(string workspacePath, string mockupPath)
    {
        var snapshots = PocSnapshots.List(workspacePath);
        if (snapshots.Count < 2)
            return new PocVersionHistory(snapshots, PocSnapshotDiff.Empty, null);

        var previous = snapshots[^2];
        var diff = PocSnapshots.Diff(
            PocSnapshots.TryReadContent(previous.FilePath),
            PocSnapshots.TryReadContent(mockupPath));

        return new PocVersionHistory(snapshots, diff, previous.Version);
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
        if (spec.Screens.Count == 0 && spec.Rules.Count == 0 && spec.WorkedExamples.Count == 0 && spec.AcceptanceCriteria.Count == 0)
            return new PocReviewCoverage(
                Array.Empty<string>(), Array.Empty<PocRuleCoverage>(), Array.Empty<string>(), Array.Empty<PocAcceptanceCoverage>());

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

        // Câu nghiệm thu ↔ kịch bản: cross-link qua AcRefs (đã được UatScenarioService chuẩn hoá về
        // dạng "AC-n" lúc lưu, nên ở đây so khớp thẳng, không phải đoán lại cách model viết mã).
        var acceptance = spec.AcceptanceCriteria.Select(ac =>
        {
            var titles = scenarios.Scenarios
                .Where(s => s.AcRefs.Any(r => string.Equals(r, ac.Ref, StringComparison.OrdinalIgnoreCase)))
                .Select(s => s.Title)
                .Distinct()
                .ToArray();
            return new PocAcceptanceCoverage(ac.Ref, ac.Feature, ac.Text, titles);
        }).ToList();

        return new PocReviewCoverage(spec.Screens, rules, worked, acceptance);
    }

    // "BR-3: đơn đã duyệt thì khóa sửa" → "BR-3" (để cross-link với UatScenario.RuleRefs). Không khớp ⇒ "".
    private static string RuleRefTag(string rule)
    {
        var m = System.Text.RegularExpressions.Regex.Match(rule ?? string.Empty, @"^\s*(BR-\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }
}
