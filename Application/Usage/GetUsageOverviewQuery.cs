using ICOGenerator.Data;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Usage;

public record MonthlyUsageItem(int Year, int Month, long PromptTokens, long CompletionTokens, long TotalTokens, int CallCount, decimal Cost);

public record ProjectUsageItem(Guid ProjectId, string ProjectName, long PromptTokens, long CompletionTokens, long TotalTokens, int CallCount, DateTime? LastCallAt, decimal Cost);

// Một dòng "Usage by department": project gắn orgUnit con được ROLL-UP về department gần nhất (đi ngược
// TargetResponsible trên bảng OrgUnits). DepartmentCode null = nhóm project chưa gắn đơn vị.
public record DepartmentUsageItem(string? DepartmentCode, string DepartmentName, int ProjectCount, long TotalTokens, int CallCount, decimal Cost);

public record ModelUsageItem(string ModelId, string ModelName, long PromptTokens, long CompletionTokens, long TotalTokens, int CallCount, decimal InputPricePerMillionTokens, decimal OutputPricePerMillionTokens, bool HasPrice, decimal Cost);

public record UsageOverviewVm(
    long TotalTokens,
    long TotalPromptTokens,
    long TotalCompletionTokens,
    int TotalCalls,
    long CurrentMonthTokens,
    decimal TotalCost,
    decimal CurrentMonthCost,
    bool HasAnyPricing,
    IReadOnlyList<MonthlyUsageItem> MonthlyUsage,
    IReadOnlyList<ModelUsageItem> ModelUsage,
    IReadOnlyList<ProjectUsageItem> ProjectUsage,
    IReadOnlyList<DepartmentUsageItem> DepartmentUsage,
    // Bộ lọc năm cho biểu đồ "Tokens per month". SelectedYear = null → xem 12 tháng gần nhất (mặc định);
    // có giá trị → xem trọn 12 tháng (01–12) của năm đó. AvailableYears: các năm có dữ liệu (giảm dần).
    int? SelectedYear,
    IReadOnlyList<int> AvailableYears);

public class GetUsageOverviewQuery
{
    private const int MonthsToShow = 12;

    private readonly AppDbContext _db;
    public GetUsageOverviewQuery(AppDbContext db) => _db = db;

    public async Task<UsageOverviewVm> ExecuteAsync(int? year = null)
    {
        // Bảng giá theo ModelId. Log chỉ lưu ModelId/ModelName dạng chuỗi (không FK), nên ta tra giá bằng
        // ModelId. Cùng một ModelId có thể có nhiều bản ghi AiModel (khác endpoint) → gộp lại, lấy bản đầu.
        var priceByModelId = (await _db.AiModels
                .AsNoTracking()
                .Select(m => new { m.ModelId, m.InputPricePerMillionTokens, m.OutputPricePerMillionTokens })
                .ToListAsync())
            .GroupBy(m => m.ModelId)
            .ToDictionary(
                g => g.Key ?? string.Empty,
                g => (Input: g.First().InputPricePerMillionTokens, Output: g.First().OutputPricePerMillionTokens),
                StringComparer.OrdinalIgnoreCase);

        // Quy token ra USD theo đơn giá của model (cùng công thức LlmCost mà BudgetGuard dùng → trần khớp số
        // hiển thị ở đây). Model không có giá (đã xóa / tự host để 0) → chi phí 0.
        decimal CostFor(string? modelId, long prompt, long completion)
            => modelId != null && priceByModelId.TryGetValue(modelId, out var p)
                ? LlmCost.Usd(prompt, completion, p.Input, p.Output)
                : 0m;

        bool HasPrice(string? modelId)
            => modelId != null && priceByModelId.TryGetValue(modelId, out var p) && (p.Input > 0 || p.Output > 0);

        var now = DateTime.UtcNow;

        // One pass over the whole call-log table, aggregated at the finest grain every section below
        // needs — project + run + month + model. Each section re-aggregates this in memory: sums, counts
        // and maxes all compose, and per-token cost is linear in (prompt, completion) for a fixed model,
        // so summing CostFor over these rows equals computing it on the coarser groups. One table scan
        // instead of four. ModelId/ModelName/ProjectName are bounded columns (≤ nvarchar(200)).
        var logRaw = await _db.AgentModelCallLogs
            .AsNoTracking()
            .GroupBy(x => new
            {
                x.ProjectId,
                ProjectName = x.Project!.Name,
                ProjectOrgUnitCode = x.Project!.OrgUnitCode,
                x.WorkflowRunId,
                x.CreatedAt.Year,
                x.CreatedAt.Month,
                x.ModelId,
                x.ModelName
            })
            .Select(g => new
            {
                g.Key.ProjectId,
                g.Key.ProjectName,
                g.Key.ProjectOrgUnitCode,
                g.Key.WorkflowRunId,
                g.Key.Year,
                g.Key.Month,
                g.Key.ModelId,
                g.Key.ModelName,
                PromptTokens = g.Sum(x => (long)x.PromptTokens),
                CompletionTokens = g.Sum(x => (long)x.CompletionTokens),
                TotalTokens = g.Sum(x => (long)x.TotalTokens),
                CallCount = g.Count(),
                LastCalledAt = g.Max(x => (DateTime?)x.CreatedAt)
            })
            .ToListAsync();

        // ----- Theo model (toàn thời gian); cũng là nguồn cộng ra tổng token + tổng chi phí -----
        // Gom theo cả ModelId + ModelName (ModelId ↔ ModelName gần như 1:1 nên bảng không bị tách dòng).
        var models = logRaw
            .GroupBy(x => new { x.ModelId, x.ModelName })
            .Select(g =>
            {
                var modelId = g.Key.ModelId;
                var promptTokens = g.Sum(x => x.PromptTokens);
                var completionTokens = g.Sum(x => x.CompletionTokens);
                var totalTokens = g.Sum(x => x.TotalTokens);
                var callCount = g.Sum(x => x.CallCount);
                var price = priceByModelId.TryGetValue(modelId ?? string.Empty, out var p) ? p : (Input: 0m, Output: 0m);
                return new ModelUsageItem(
                    modelId ?? string.Empty,
                    string.IsNullOrWhiteSpace(g.Key.ModelName) ? (modelId ?? "(unknown)") : g.Key.ModelName,
                    promptTokens,
                    completionTokens,
                    totalTokens,
                    callCount,
                    price.Input,
                    price.Output,
                    HasPrice(modelId),
                    CostFor(modelId, promptTokens, completionTokens));
            })
            .OrderByDescending(x => x.Cost)
            .ThenByDescending(x => x.TotalTokens)
            .ToList();

        var totalTokens = models.Sum(x => x.TotalTokens);
        var totalPrompt = models.Sum(x => x.PromptTokens);
        var totalCompletion = models.Sum(x => x.CompletionTokens);
        var totalCalls = models.Sum(x => x.CallCount);
        var totalCost = models.Sum(x => x.Cost);

        // Các năm có dữ liệu, để đổ vào dropdown chọn năm; luôn kèm năm hiện tại dù chưa có log nào.
        var availableYears = logRaw.Select(x => x.Year)
            .Append(now.Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToList();

        // year hợp lệ khi có trong danh sách năm; ngược lại (null hoặc năm lạ) → về mặc định "12 tháng gần nhất".
        var selectedYear = year is int y && availableYears.Contains(y) ? y : (int?)null;

        // ----- Theo tháng -----
        // Không chọn năm → cửa sổ trượt 12 tháng kết thúc ở tháng hiện tại. Chọn năm → trọn 01–12 của năm đó.
        var firstMonth = selectedYear is int sy
            ? new DateTime(sy, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            : new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(MonthsToShow - 1));

        // rows ngoài cửa sổ tự bị loại vì vòng lặp chỉ duyệt 12 tháng.
        var monthly = Enumerable.Range(0, MonthsToShow)
            .Select(offset =>
            {
                var month = firstMonth.AddMonths(offset);
                var rows = logRaw.Where(x => x.Year == month.Year && x.Month == month.Month).ToList();
                return new MonthlyUsageItem(
                    month.Year,
                    month.Month,
                    rows.Sum(x => x.PromptTokens),
                    rows.Sum(x => x.CompletionTokens),
                    rows.Sum(x => x.TotalTokens),
                    rows.Sum(x => x.CallCount),
                    rows.Sum(x => CostFor(x.ModelId, x.PromptTokens, x.CompletionTokens)));
            })
            .ToList();

        // Token/chi phí THÁNG HIỆN TẠI tính độc lập với cửa sổ đang xem (xem năm quá khứ không làm sai số này).
        var currentMonthRows = logRaw.Where(x => x.Year == now.Year && x.Month == now.Month).ToList();
        var currentMonthTokens = currentMonthRows.Sum(x => x.TotalTokens);
        var currentMonthCost = currentMonthRows.Sum(x => CostFor(x.ModelId, x.PromptTokens, x.CompletionTokens));

        // ----- Theo project (gộp lại từ logRaw) -----
        var projects = logRaw
            .GroupBy(x => new { x.ProjectId, x.ProjectName })
            .Select(g => new ProjectUsageItem(
                g.Key.ProjectId,
                g.Key.ProjectName,
                g.Sum(x => x.PromptTokens),
                g.Sum(x => x.CompletionTokens),
                g.Sum(x => x.TotalTokens),
                g.Sum(x => x.CallCount),
                g.Max(x => x.LastCalledAt),
                g.Sum(x => CostFor(x.ModelId, x.PromptTokens, x.CompletionTokens))))
            .OrderByDescending(x => x.TotalTokens)
            .ToList();

        // ----- Theo phòng ban: roll-up orgUnit con của project về department gần nhất -----
        var departments = await BuildDepartmentUsageAsync(logRaw
            .Select(x => (x.ProjectId, x.ProjectOrgUnitCode, x.TotalTokens, x.CallCount, Cost: CostFor(x.ModelId, x.PromptTokens, x.CompletionTokens)))
            .ToList());

        var hasAnyPricing = priceByModelId.Values.Any(p => p.Input > 0 || p.Output > 0);

        return new UsageOverviewVm(
            totalTokens,
            totalPrompt,
            totalCompletion,
            totalCalls,
            currentMonthTokens,
            totalCost,
            currentMonthCost,
            hasAnyPricing,
            monthly,
            models,
            projects,
            departments,
            selectedYear,
            availableYears);
    }

    // Gom chi phí về ĐƠN VỊ CẤP PHÒNG BAN: project thường gắn một orgUnit con (line/nhóm), nên đi ngược
    // TargetResponsible tới department gần nhất (IsDepartment). Không tìm được department trên đường đi
    // (chuỗi cấp trên trỏ ra ngoài dữ liệu) thì giữ chính orgUnit đó làm nhóm; mã không còn tồn tại hoặc
    // project chưa gắn đơn vị rơi vào nhóm "(Chưa gắn đơn vị)".
    private async Task<IReadOnlyList<DepartmentUsageItem>> BuildDepartmentUsageAsync(
        IReadOnlyList<(Guid ProjectId, string? OrgUnitCode, long TotalTokens, int CallCount, decimal Cost)> rows)
    {
        if (rows.Count == 0)
            return Array.Empty<DepartmentUsageItem>();

        // Bảng OrgUnits nhỏ (vài trăm dòng) — nạp một lần, dựng map cha-con trong RAM. Mã trùng lấy bản đầu.
        var units = (await _db.OrgUnits.AsNoTracking()
                .Where(u => !u.IsDelete && u.OrgUnitCode != null && u.OrgUnitCode != "")
                .Select(u => new { Code = u.OrgUnitCode!, u.DisplayName, u.TargetResponsible, u.IsDepartment })
                .ToListAsync())
            .GroupBy(u => u.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(u => u.Code, u => u, StringComparer.OrdinalIgnoreCase);

        (string? Code, string Name) ResolveDepartment(string? orgUnitCode)
        {
            if (string.IsNullOrWhiteSpace(orgUnitCode) || !units.TryGetValue(orgUnitCode.Trim(), out var unit))
                return (null, "(Chưa gắn đơn vị)");

            var current = unit;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.Code };
            while (!current.IsDepartment
                   && !string.IsNullOrWhiteSpace(current.TargetResponsible)
                   && units.TryGetValue(current.TargetResponsible!, out var parent)
                   && visited.Add(parent.Code))
            {
                current = parent;
            }

            var resolved = current.IsDepartment ? current : unit;
            return (resolved.Code, string.IsNullOrWhiteSpace(resolved.DisplayName) ? resolved.Code : resolved.DisplayName!);
        }

        return rows
            .GroupBy(r => ResolveDepartment(r.OrgUnitCode))
            .Select(g => new DepartmentUsageItem(
                g.Key.Code,
                g.Key.Name,
                g.Select(r => r.ProjectId).Distinct().Count(),
                g.Sum(r => r.TotalTokens),
                g.Sum(r => r.CallCount),
                g.Sum(r => r.Cost)))
            .OrderByDescending(x => x.TotalTokens)
            .ToList();
    }
}
