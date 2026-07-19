using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public record RequirementWorkspaceResult(
    Project Project,
    string SelectedVersion,
    bool BaModelSupportsVision,
    IReadOnlyList<CoverageMapItem> Coverage,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> PlannedScope,
    IReadOnlyList<string> WorkedExamples,
    IReadOnlyList<string> SpecAssumptions,
    string? SpecVersion);

public class GetRequirementWorkspaceQuery
{
    private readonly AppDbContext _db;
    private readonly ICOGenerator.Services.Artifacts.IProjectArtifactCatalog _artifactCatalog;

    public GetRequirementWorkspaceQuery(AppDbContext db, ICOGenerator.Services.Artifacts.IProjectArtifactCatalog artifactCatalog)
    {
        _db = db;
        _artifactCatalog = artifactCatalog;
    }

    public async Task<RequirementWorkspaceResult?> ExecuteAsync(Guid projectId, string? version = null)
    {
        // Chỉ đọc để render màn hình workspace (controller trả thẳng vào View, không SaveChanges trên đồ
        // thị này) ⇒ AsNoTracking để khỏi tốn change-tracker cho cả Project + Documents + Conversations +
        // WorkflowRuns được Include bên dưới.
        // AsSplitQuery: nhiều collection Include trên cùng một query single-query sẽ JOIN chéo thành tích
        // Descartes |Conversations| × |WorkflowRuns| × |SourceFiles| dòng — trang này reload sau MỖI lượt
        // chat (Chat redirect về Index) nên hội thoại càng dài càng phình. Tách mỗi collection một query.
        var project = await _db.Projects
            .AsNoTracking()
            .Include(x => x.Conversations.OrderBy(c => c.CreatedAt))
            .Include(x => x.WorkflowRuns.OrderBy(w => w.CreatedAt))
            .Include(x => x.SourceFiles.OrderByDescending(s => s.CreatedAt))
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == projectId);

        if (project == null)
            return null;

        // Documents nạp riêng và KHÔNG kéo cột Content: trang chỉ liệt kê tab theo FileName/VersionName,
        // nội dung preview được fetch on-demand qua DocumentPreview — kéo Content (nvarchar(max)) của MỌI
        // phiên bản tài liệu ở mỗi lần vào trang là phần nặng nhất của query cũ mà không ai đọc.
        project.Documents = (await _db.ProjectDocuments
                .AsNoTracking()
                .Where(d => d.ProjectId == projectId)
                .Select(d => new
                {
                    d.Id, d.ProjectId, d.AgentId, d.Folder, d.VersionName,
                    d.IsApproved, d.FileName, d.FilePath, d.TokenUsed, d.CreatedAt
                })
                .ToListAsync())
            .Select(d => new ProjectDocument
            {
                Id = d.Id,
                ProjectId = d.ProjectId,
                AgentId = d.AgentId,
                Folder = d.Folder,
                VersionName = d.VersionName,
                IsApproved = d.IsApproved,
                FileName = d.FileName,
                FilePath = d.FilePath,
                TokenUsed = d.TokenUsed,
                CreatedAt = d.CreatedAt
            })
            .ToList();

        // Cờ vision của model BA đang cấu hình: dùng để cảnh báo trên UI rằng ảnh sẽ KHÔNG được model đọc
        // (chỉ phần text của PDF được dùng) khi model hiện tại không hỗ trợ vision.
        var baSupportsVision = await _db.Agents
            .AsNoTracking()
            .Where(a => a.RoleKey == AgentRoleKey.BusinessAnalyst && a.AiModel != null)
            .Select(a => a.AiModel!.SupportsVision)
            .FirstOrDefaultAsync();

        var selectedVersion = version;
        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            selectedVersion = project.Documents.Any(x => x.VersionName == "draft")
                ? "draft"
                : project.Documents
                    .Where(x => x.VersionName.StartsWith("V"))
                    .OrderByDescending(x => int.TryParse(x.VersionName.Replace("V", ""), out var n) ? n : 0)
                    .Select(x => x.VersionName)
                    .FirstOrDefault();
        }

        // Giả định của AI Design Spec mới nhất (nếu đã sinh): spec được phép tự đưa giả định rồi đi
        // thẳng vào bước dựng POC, nên panel này là chỗ duy nhất user thấy chúng trước khi xem POC.
        // Chỉ kéo Content của ĐÚNG một document spec mới nhất (không đụng đường ProjectDocuments ở trên
        // vốn cố tình bỏ Content).
        var latestSpec = await _db.ProjectDocuments
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.FileName == _artifactCatalog.AiDesignSpec.FileName)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new { d.Content, d.VersionName })
            .FirstOrDefaultAsync();

        // Panel tiến độ khai thác + "Điều đã chốt" cạnh khung chat: parse từ hai cột text trên Project
        // (đã nạp sẵn ở query trên — không thêm round-trip DB nào).
        return new RequirementWorkspaceResult(
            project,
            selectedVersion ?? "draft",
            baSupportsVision,
            CoverageMapParser.Parse(project.RequirementCoverageMap),
            DecisionLogService.ParseItems(project.DecisionLog),
            InterviewOutlookService.ParseItems(project.OpenQuestions),
            InterviewOutlookService.ParseItems(project.PlannedScope),
            InterviewOutlookService.ParseItems(project.WorkedExamples),
            SpecAssumptionsParser.Parse(latestSpec?.Content),
            latestSpec?.VersionName);
    }
}
