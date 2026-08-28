using System.Linq.Expressions;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>Giai đoạn của một dự án + cờ "đang có bản nháp Brief chờ duyệt". Xem <see cref="ProjectStatusResolver"/>.</summary>
/// <param name="HasPendingBriefDraft">
/// true khi dự án ĐÃ duyệt Brief nhưng lại đang có một bản nháp mới chưa duyệt (vòng soạn lại từ ghi chú
/// POC / góp ý trên Brief). Là một cờ RIÊNG chứ không kéo trạng thái tụt về <see cref="ProjectStatus.ProductBriefDraft"/>:
/// mỗi vòng góp ý sẽ làm badge nhảy tới nhảy lui giữa hai chặng, trong khi cái đã đạt thì không mất đi.
/// </param>
public record ProjectStatusRow(Guid ProjectId, ProjectStatus Status, bool HasPendingBriefDraft);

/// <summary>
/// SUY RA <see cref="ProjectStatus"/> của dự án từ dữ liệu đã có — không có cột nào lưu sẵn (lý do ở
/// XML doc của <see cref="ProjectStatus"/>).
///
/// <para>
/// <b>Luật nằm ở đúng một chỗ</b>: <see cref="Rule"/>. Nó là một <see cref="Expression"/> nên chạy được
/// DƯỚI DB — badge của trang danh sách và (sau này) báo cáo đếm theo trạng thái dùng CHUNG một biểu thức,
/// không có bản sao nào để trôi lệch. Cả năm chặng chỉ cần một cột + ba phép EXISTS, và FK
/// <c>ProjectId</c> của cả hai bảng con đều đã có index.
/// </para>
///
/// <para>
/// <b>Thứ tự xét là "chặng CAO NHẤT đã đạt"</b>, không phải "chặng đang làm": một dự án đã duyệt Brief
/// rồi soạn lại bản nháp mới vẫn là <see cref="ProjectStatus.ProductBriefApproved"/> (kèm
/// <see cref="ProjectStatusRow.HasPendingBriefDraft"/>).
/// </para>
///
/// <para>
/// ⚠️ <b>Phải chạy dưới <c>IgnoreQueryFilters()</c></b> — và đó là lý do luật không được phơi ra cho nơi
/// gọi tự ghép vào query của mình. <c>AgentConversation</c> có global query filter
/// <c>ArchivedAt == null</c>, trong khi "＋ New Chat" chỉ đóng dấu <c>ArchivedAt</c> chứ không xoá
/// (<c>StartNewChatUseCase</c>). Không bỏ filter thì một dự án đã phỏng vấn cả buổi rồi bấm New Chat sẽ
/// rơi ngược về <see cref="ProjectStatus.New"/> — con số vẫn ra, chỉ là sai, và không có gì báo động.
/// "Chưa chat gì hết" ở đây nghĩa là chưa TỪNG chat.
/// </para>
/// </summary>
public class ProjectStatusResolver
{
    private readonly AppDbContext _db;
    private readonly IProjectArtifactCatalog _artifactCatalog;

    public ProjectStatusResolver(AppDbContext db, IProjectArtifactCatalog artifactCatalog)
    {
        _db = db;
        _artifactCatalog = artifactCatalog;
    }

    /// <summary>
    /// NGUỒN CHÂN LÝ DUY NHẤT của luật xếp chặng, dạng biểu thức để EF dịch thẳng xuống SQL (CASE + EXISTS).
    /// Tham số là tên file Product Brief (<see cref="IProjectArtifactCatalog"/>) — truyền vào thay vì đọc
    /// trong thân biểu thức để không kéo cả catalog vào cây expression.
    /// </summary>
    public static Expression<Func<Project, ProjectStatusRow>> Rule(string productBriefFileName) =>
        project => new ProjectStatusRow(
            project.Id,
            project.PocAcceptedAtUtc != null
                ? ProjectStatus.PocApproved
                : project.Documents.Any(d => d.FileName == productBriefFileName && d.IsApproved)
                    ? ProjectStatus.ProductBriefApproved
                    : project.Documents.Any(d => d.FileName == productBriefFileName && !d.IsApproved)
                        ? ProjectStatus.ProductBriefDraft
                        : project.Conversations.Any()
                            ? ProjectStatus.GetRequirement
                            : ProjectStatus.New,
            project.Documents.Any(d => d.FileName == productBriefFileName && d.IsApproved)
                && project.Documents.Any(d => d.FileName == productBriefFileName && !d.IsApproved));

    /// <summary>Chặng của một loạt dự án trong đúng một truy vấn. Id không tồn tại thì không có trong dict.</summary>
    public async Task<IReadOnlyDictionary<Guid, ProjectStatusRow>> ResolveManyAsync(
        IEnumerable<Guid> projectIds,
        CancellationToken cancellationToken = default)
    {
        var wanted = projectIds.Distinct().ToList();
        if (wanted.Count == 0)
            return new Dictionary<Guid, ProjectStatusRow>();

        var rows = await _db.Projects.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(p => wanted.Contains(p.Id))
            .Select(Rule(_artifactCatalog.ProductBrief.FileName))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.ProjectId);
    }

    /// <summary>Chặng của một dự án; null khi không có dự án nào mang id này.</summary>
    public async Task<ProjectStatusRow?> ResolveAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var map = await ResolveManyAsync(new[] { projectId }, cancellationToken);
        return map.TryGetValue(projectId, out var row) ? row : null;
    }
}
