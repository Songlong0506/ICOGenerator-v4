using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public class GetProjectListQuery
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;

    public GetProjectListQuery(AppDbContext db, WorkspacePathResolver workspacePathResolver)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
    }

    public const int DefaultPageSize = 10;

    public async Task<ProjectListPage> ExecuteAsync(
        int page = 1,
        int pageSize = DefaultPageSize,
        string? username = null,
        bool canViewAll = false,
        string? orgUnitCode = null,
        ProjectStatus? status = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;

        IQueryable<Domain.Project> projects = _db.Projects.AsNoTracking();

        // Người không có quyền ProjectsViewAll (User thường) chỉ thấy project do chính mình tạo. Project
        // "không có chủ" (CreatedByUsername == null, tạo trước khi có tính năng này) không hiện cho họ.
        if (!canViewAll)
            projects = projects.Where(x => x.CreatedByUsername != null && x.CreatedByUsername == username);

        // Bộ lọc của trang: theo đơn vị yêu cầu (OrgUnitCode) và/hoặc trạng thái. Chuỗi rỗng coi như không
        // lọc; áp trước khi phân trang để tổng số & số trang khớp với kết quả đã lọc.
        var normalizedOrgUnitCode = string.IsNullOrWhiteSpace(orgUnitCode) ? null : orgUnitCode.Trim();
        if (normalizedOrgUnitCode != null)
            projects = projects.Where(x => x.OrgUnitCode == normalizedOrgUnitCode);
        if (status.HasValue)
            projects = projects.Where(x => x.Status == status.Value);

        var baseQuery = projects.OrderByDescending(x => x.CreatedAt);

        var totalCount = await baseQuery.CountAsync();

        // Lấy status/stage của workflow run MỚI NHẤT bằng subquery ở DB thay vì Include toàn bộ
        // WorkflowRuns về RAM — tránh kéo dữ liệu thừa với project chạy nhiều lần.
        var rows = await baseQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(project => new
            {
                Project = project,
                LatestWorkflow = project.WorkflowRuns
                    .OrderByDescending(w => w.CreatedAt)
                    .Select(w => new { w.Status, w.CurrentStage })
                    .FirstOrDefault()
            })
            .ToListAsync();

        // Một lượt quét nhẹ bảng OrgUnits (~vài trăm dòng) phục vụ cả hai nhu cầu của trang: tra tên đơn
        // vị cho các project đang hiển thị và danh sách chọn của modal New Project. Mã trùng (dữ liệu
        // đồng bộ lỗi) lấy bản đầu để không vỡ dropdown.
        var orgUnits = (await _db.OrgUnits.AsNoTracking()
                .Where(u => !u.IsDelete && u.OrgUnitCode != null && u.OrgUnitCode != ""
                            && u.DisplayName != null && u.DisplayName != "")
                .Select(u => new { u.OrgUnitCode, u.DisplayName, u.IsDepartment })
                .ToListAsync())
            .GroupBy(u => u.OrgUnitCode!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(u => new OrgUnitOption(u.OrgUnitCode!, u.DisplayName!, u.IsDepartment))
            // Dropdown giờ là danh sách phẳng có ô tìm kiếm (không phân cấp Department/OrgUnit),
            // nên chỉ cần xếp theo tên cho dễ tra.
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var orgUnitNameByCode = orgUnits.ToDictionary(o => o.Code, o => o.Name, StringComparer.OrdinalIgnoreCase);

        var items = rows
            .Select(row =>
            {
                var latestWorkflow = row.LatestWorkflow;
                var hasRunningWorkflow = latestWorkflow is not null
                    && latestWorkflow.Status is not WorkflowRunStatus.Completed
                        and not WorkflowRunStatus.Failed
                        and not WorkflowRunStatus.Canceled;

                var projectKey = WorkspacePathResolver.GetWorkspaceFolder(row.Project.Id, row.Project.Name);

                return new ProjectListItem(
                    row.Project,
                    File.Exists(_workspacePathResolver.GetMockupPath(projectKey)),
                    latestWorkflow?.Status.ToString(),
                    latestWorkflow?.CurrentStage.ToString(),
                    hasRunningWorkflow,
                    row.Project.OrgUnitCode != null
                        ? orgUnitNameByCode.GetValueOrDefault(row.Project.OrgUnitCode)
                        : null);
            })
            .ToList();

        return new ProjectListPage(items, page, pageSize, totalCount, orgUnits, normalizedOrgUnitCode, status);
    }
}
