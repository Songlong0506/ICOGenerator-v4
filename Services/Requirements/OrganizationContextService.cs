using System.Text;
using System.Text.RegularExpressions;
using ICOGenerator.Data;
using ICOGenerator.Services.Organization;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Render "bối cảnh tổ chức Bosch" (~nửa trang) từ dữ liệu HR thật (bảng <c>OrgUnits</c>/<c>Associates</c>,
/// đồng bộ từ HR_Portal) để đính vào prompt BA — cả lượt chat lẫn bước soạn tài liệu: danh sách department
/// + HoD, quy mô nhân sự, chức danh phổ biến, cùng ghi chú "đơn vị yêu cầu" của từng project. Đi kèm là
/// hai khối TĨNH cùng loại "hằng số của sản phẩm": "ranh giới phạm vi" (<see cref="BuildScopeNote"/>) chặn
/// BA gợi ý những phạm vi không có thật ("Toàn Bosch Việt Nam", "toàn tập đoàn"…), và "nền tảng đã chốt"
/// (<see cref="BuildPlatformNote"/>) chặn BA hỏi/gợi ý những thứ nhà máy đã chốt sẵn (kênh thông báo, cách
/// đăng nhập, nguồn của dữ liệu orgUnit/nhân sự).
///
/// Nguyên tắc dữ liệu: prompt CHỈ nhận dữ liệu GỘP (tên đơn vị, số lượng, chức danh) — KHÔNG bao giờ đưa
/// thông tin cá nhân nhạy cảm của Associates (ngày sinh, điện thoại, email, địa chỉ đón) vào prompt. Tên
/// người thật chỉ xuất hiện ở vai trò quản lý (HoD/manager) — thứ vốn ghi công khai trong tài liệu dự án.
///
/// Bản render dùng chung mọi project nên cache theo tiến trình (IMemoryCache, hết hạn theo thời gian —
/// dữ liệu HR chỉ đổi khi đồng bộ lại). Fail-open toàn tuyến: bảng trống/lỗi DB ⇒ trả null, chat và sinh
/// tài liệu tiếp tục như khi chưa có tính năng này.
/// </summary>
public partial class OrganizationContextService
{
    private const string TemplatePath = "BusinessAnalyst/organization-context.v2.md";
    private const string ScopeTemplatePath = "BusinessAnalyst/organization-scope.v1.md";
    private const string PlatformTemplatePath = "BusinessAnalyst/organization-platform.v1.md";
    private const string CacheKey = "OrganizationContext.BaContext";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    // Đủ để BA nhận diện các nhóm nghề chính trong nhà máy mà phần chức danh vẫn gọn một đoạn.
    private const int TopPositionCount = 12;

    private readonly AppDbContext _db;
    private readonly PromptTemplateService _prompts;
    private readonly OrgChartProvider _orgChart;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OrganizationContextService> _logger;

    public OrganizationContextService(
        AppDbContext db,
        PromptTemplateService prompts,
        OrgChartProvider orgChart,
        IMemoryCache cache,
        ILogger<OrganizationContextService> logger)
    {
        _db = db;
        _prompts = prompts;
        _orgChart = orgChart;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Bức tranh tổ chức dùng chung (cache): template tĩnh + department/HoD/quy mô/chức danh render từ DB.
    /// Trả null khi chưa có dữ liệu OrgUnits hoặc khi render lỗi — caller cứ bỏ qua phần ngữ cảnh này.
    /// </summary>
    public virtual async Task<string?> BuildBaContextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _cache.GetOrCreateAsync(CacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheDuration;
                return RenderBaContextAsync(cancellationToken);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không render được bối cảnh tổ chức từ OrgUnits/Associates — tiếp tục không có phần ngữ cảnh này.");
            return null;
        }
    }

    /// <summary>
    /// Ranh giới phạm vi của sản phẩm: mọi ứng dụng sinh ra ở đây chỉ phục vụ nhà máy Bosch Đồng Nai, nên
    /// BA bị CẤM đưa phương án vượt khỏi nhà máy ("Toàn Bosch Việt Nam", "toàn tập đoàn"…) và được cho sẵn
    /// thang phạm vi hợp lệ (orgUnit → department → toàn nhà máy) để gợi ý bằng tên đơn vị có thật.
    ///
    /// Đây là sự thật nghiệp vụ của sản phẩm chứ không suy ra từ dữ liệu HR, nên khối này đi kèm MỌI lời
    /// gọi BA — kể cả khi <c>OrgUnits</c> còn trống và <see cref="BuildBaContextAsync"/> trả null. Nội dung
    /// nằm ở template tĩnh; đọc lỗi ⇒ null (fail-open) như các phần ngữ cảnh khác.
    /// </summary>
    public virtual string? BuildScopeNote() => ReadStaticBlock(ScopeTemplatePath, "ranh giới phạm vi");

    /// <summary>
    /// Nền tảng đã chốt của môi trường nhà máy, ba ràng buộc cùng hạng: (1) chỉ có DUY NHẤT kênh thông báo
    /// EMAIL, nên BA bị CẤM hỏi "muốn báo qua kênh nào" và cấm gợi ý những kênh không tồn tại (Teams, SMS,
    /// Zalo, thông báo đẩy…) — nhóm "Thông báo / nhắc nhở" chỉ còn hỏi AI nhận và KHI NÀO; (2) chỉ đăng
    /// nhập bằng SSO qua IdentityServer; (3) danh sách orgUnit và nhân sự của MỌI ứng dụng trong nhà máy
    /// đồng bộ tự động từ hệ thống COMPAS, nên cấm hỏi ai quản lý/cập nhật hai danh mục đó và cấm dựng màn
    /// hình quản lý chúng.
    ///
    /// Cùng hạng với <see cref="BuildScopeNote"/>: sự thật của môi trường chứ không suy ra từ dữ liệu HR,
    /// nên đi kèm MỌI lời gọi BA kể cả khi <c>OrgUnits</c> còn trống; đọc lỗi ⇒ null (fail-open). Tách
    /// thành template riêng vì hai khối được sửa vì hai lý do khác nhau.
    /// </summary>
    public virtual string? BuildPlatformNote() => ReadStaticBlock(PlatformTemplatePath, "nền tảng đã chốt");

    /// <summary>
    /// Đọc một khối ngữ cảnh TĨNH từ template và cắt khối comment HTML đầu file (ghi chú dành cho người
    /// sửa file, không phải chỉ dẫn cho model). Trả null khi rỗng hoặc đọc lỗi — caller cứ bỏ qua khối này.
    /// </summary>
    private string? ReadStaticBlock(string templatePath, string blockName)
    {
        try
        {
            var block = HtmlCommentRegex().Replace(_prompts.Get(templatePath), string.Empty).Trim();
            return block.Length == 0 ? null : block;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không đọc được khối {BlockName} {TemplatePath} — tiếp tục không có phần ngữ cảnh này.", blockName, templatePath);
            return null;
        }
    }

    /// <summary>
    /// Ghi chú "đơn vị yêu cầu" cho MỘT project đã gắn <c>Project.OrgUnitCode</c>: tên orgUnit + manager,
    /// và department cha (đi ngược <c>TargetResponsible</c>) + HoD. Trả null khi project chưa gắn đơn vị,
    /// mã không còn tồn tại, hoặc tra cứu lỗi (fail-open).
    /// </summary>
    public virtual async Task<string?> BuildProjectUnitNoteAsync(string? orgUnitCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orgUnitCode))
            return null;

        try
        {
            var chart = await _orgChart.GetAsync(cancellationToken);
            var unit = chart.Find(orgUnitCode);
            if (unit == null)
                return null;

            // Department chứa đơn vị này; null khi chuỗi cấp trên không dẫn tới department nào — khi đó
            // ghi chú chỉ nói về chính orgUnit, không bịa ra phòng ban.
            var department = chart.FindDepartment(orgUnitCode);

            var managerNames = await LoadManagerNamesAsync(new[] { unit.ManagerId, department?.ManagerId }, cancellationToken);
            string ManagerLabel(string? managerId) =>
                managerId != null && managerNames.TryGetValue(managerId, out var name) ? name : "(chưa rõ)";

            var sb = new StringBuilder();
            sb.AppendLine("### Đơn vị yêu cầu của dự án này (đã gắn khi tạo project)");
            if (unit.IsDepartment)
            {
                sb.AppendLine($"- {unit.DisplayName} (mã {unit.Code}) là một department — HoD: {ManagerLabel(unit.ManagerId)}.");
            }
            else
            {
                sb.AppendLine($"- OrgUnit {unit.DisplayName} (mã {unit.Code}) — manager: {ManagerLabel(unit.ManagerId)}.");
                if (department != null)
                    sb.AppendLine($"- Thuộc department {department.DisplayName} — HoD: {ManagerLabel(department.ManagerId)}.");
            }
            sb.Append("- Mặc định ngữ cảnh nghiệp vụ/người dùng xoay quanh đơn vị này; vẫn xác nhận lại trong hội thoại, không tự suy rộng.");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không dựng được ghi chú đơn vị yêu cầu cho mã orgUnit {OrgUnitCode}.", orgUnitCode);
            return null;
        }
    }

    /// <summary>
    /// Khối ngữ cảnh tổ chức hoàn chỉnh cho một project: ranh giới phạm vi + nền tảng đã chốt (hai khối
    /// hằng số, luôn có) + bức tranh chung (cache) + ghi chú "đơn vị yêu cầu" nếu project đã gắn
    /// <c>OrgUnitCode</c>. Trả chuỗi rỗng khi không dựng được phần nào (fail-open) — caller cứ bỏ qua
    /// phần ngữ cảnh này.
    /// </summary>
    public async Task<string> BuildCombinedContextAsync(string? projectOrgUnitCode, CancellationToken cancellationToken = default)
        => Combine(
            BuildScopeNote(),
            BuildPlatformNote(),
            await BuildBaContextAsync(cancellationToken),
            await BuildProjectUnitNoteAsync(projectOrgUnitCode, cancellationToken));

    /// <summary>Ghép các phần ngữ cảnh tổ chức (bỏ phần trống) thành một khối; trả chuỗi rỗng khi không có gì.</summary>
    private static string Combine(params string?[] sections)
        => string.Join("\n\n", sections.Where(s => !string.IsNullOrWhiteSpace(s)));

    private async Task<string?> RenderBaContextAsync(CancellationToken cancellationToken)
    {
        var units = (await _orgChart.GetAsync(cancellationToken)).Units;
        if (units.Count == 0)
            return null;

        var now = DateTime.UtcNow;
        // Chỉ nhân sự ĐANG hoạt động, và chỉ kéo số liệu gộp về (đếm theo orgUnit / theo chức danh) —
        // không một hồ sơ cá nhân nào rời khỏi DB ở đây.
        var activeAssociates = _db.Associates.AsNoTracking()
            .Where(a => !a.IsDelete && (a.LeavingDate == null || a.LeavingDate > now));

        var headcountByUnit = (await activeAssociates
                .Where(a => a.OrgUnitCode != null)
                .GroupBy(a => a.OrgUnitCode!)
                .Select(g => new { Code = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Code, x => x.Count, StringComparer.OrdinalIgnoreCase);

        var topPositions = await activeAssociates
            .Where(a => a.Position != null && a.Position != "")
            .GroupBy(a => a.Position!)
            .Select(g => new { Position = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(TopPositionCount)
            .ToListAsync(cancellationToken);

        var totalHeadcount = await activeAssociates.CountAsync(cancellationToken);

        var departments = units.Where(u => u.IsDepartment)
            .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var managerNames = await LoadManagerNamesAsync(departments.Select(d => d.ManagerId), cancellationToken);

        // Cây con của mỗi department: gom orgUnit theo cấp trên trực tiếp rồi loang từ department xuống
        // (visited chặn chu trình). Headcount của department = tổng nhân sự mọi orgUnit trong cây con.
        var childrenByParent = units
            .Where(u => !string.IsNullOrWhiteSpace(u.ParentCode))
            .GroupBy(u => u.ParentCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(u => u.Code).ToList(), StringComparer.OrdinalIgnoreCase);

        var deptSection = new StringBuilder();
        deptSection.AppendLine("### Các phòng ban (department) và HoD");
        foreach (var dept in departments)
        {
            var (subUnitCount, headcount) = MeasureSubtree(dept.Code, childrenByParent, headcountByUnit);
            var hod = dept.ManagerId != null && managerNames.TryGetValue(dept.ManagerId, out var name) ? name : "(chưa rõ)";
            deptSection.AppendLine($"- {dept.DisplayName} (mã {dept.Code}) — HoD: {hod} — {subUnitCount} orgUnit trực thuộc, ~{headcount} nhân sự internal.");
        }
        if (departments.Count == 0)
            deptSection.AppendLine("(Chưa có orgUnit nào được đánh dấu là department trong dữ liệu HR.)");

        var positionSection = new StringBuilder();
        positionSection.AppendLine("### Chức danh phổ biến (nhân sự đang hoạt động)");
        positionSection.Append(topPositions.Count == 0
            ? "(Chưa có dữ liệu chức danh.)"
            : string.Join(", ", topPositions.Select(p => $"{p.Position} ({p.Count})")) + ".");

        var totals = $"### Quy mô\nToàn bộ dữ liệu HR hiện có {units.Count} orgUnit và {totalHeadcount} nhân sự internal đang hoạt động (KHÔNG gồm nhân viên external thuê ngoài).";

        // Khối comment HTML đầu template là ghi chú cho người sửa file — cắt bỏ TRƯỚC khi thay placeholder
        // (comment được phép nhắc tên placeholder mà không bị chèn dữ liệu trùng), model không thấy nó.
        var template = HtmlCommentRegex().Replace(_prompts.Get(TemplatePath), string.Empty);
        return template.Trim()
            .Replace("{{DEPARTMENTS}}", deptSection.ToString().TrimEnd())
            .Replace("{{POSITIONS}}", positionSection.ToString())
            .Replace("{{TOTALS}}", totals);
    }

    private static (int SubUnitCount, int Headcount) MeasureSubtree(
        string rootCode,
        IReadOnlyDictionary<string, List<string>> childrenByParent,
        IReadOnlyDictionary<string, int> headcountByUnit)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootCode };
        var queue = new Queue<string>();
        queue.Enqueue(rootCode);
        var headcount = headcountByUnit.GetValueOrDefault(rootCode);

        while (queue.Count > 0)
        {
            if (!childrenByParent.TryGetValue(queue.Dequeue(), out var children))
                continue;
            foreach (var child in children)
            {
                if (!visited.Add(child))
                    continue;
                headcount += headcountByUnit.GetValueOrDefault(child);
                queue.Enqueue(child);
            }
        }

        return (visited.Count - 1, headcount);
    }

    private async Task<Dictionary<string, string>> LoadManagerNamesAsync(IEnumerable<string?> personalNumbers, CancellationToken cancellationToken)
    {
        var ids = personalNumbers
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Chỉ lấy đúng cặp (PersonalNumber, DisplayName) của các quản lý cần hiển thị — không kéo gì thêm.
        return (await _db.Associates.AsNoTracking()
                .Where(a => !a.IsDelete && a.PersonalNumber != null && ids.Contains(a.PersonalNumber))
                .Select(a => new { a.PersonalNumber, a.DisplayName })
                .ToListAsync(cancellationToken))
            .Where(a => !string.IsNullOrWhiteSpace(a.DisplayName))
            .GroupBy(a => a.PersonalNumber!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DisplayName!, StringComparer.OrdinalIgnoreCase);
    }

    // Khối comment HTML đầu template (ghi chú cho người sửa file) — cắt bỏ trước khi thay placeholder.
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();
}
