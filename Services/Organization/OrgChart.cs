using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ICOGenerator.Services.Organization;

/// <summary>
/// Một đơn vị trong cây tổ chức, rút gọn về đúng phần cần để đi ngược lên department. Không phải entity
/// <see cref="Domain.OrgUnit"/>: bảng đó đồng bộ từ HR_Portal và mang nhiều cột không liên quan tới việc
/// định vị đơn vị trong cây.
/// </summary>
public sealed record OrgUnitNode(string Code, string DisplayName, string? ParentCode, string? ManagerId, bool IsDepartment);

/// <summary>
/// Cây tổ chức đọc từ <c>OrgUnits</c> — nơi DUY NHẤT biết cách đi từ một orgUnit lá lên department chứa nó.
///
/// <para>
/// Trước đây phép đi ngược này tồn tại HAI bản gần y hệt (ghi chú "đơn vị yêu cầu" của BA và roll-up phòng
/// ban ở trang Usage), và bucket checklist sắp cần bản thứ ba. Ba bản đi lệch nhau nghĩa là cùng một dự án
/// bị xếp vào hai phòng ban khác nhau tùy chỗ hỏi — nên chúng gộp về đây.
/// </para>
///
/// <para>
/// Bất biến sau khi dựng (chỉ đọc), nên an toàn khi cache và dùng chung nhiều request.
/// </para>
/// </summary>
public sealed class OrgChart
{
    private readonly Dictionary<string, OrgUnitNode> _byCode;

    public OrgChart(IEnumerable<OrgUnitNode> units)
    {
        // Bản ghi trùng mã (nếu dữ liệu đồng bộ lỗi) lấy bản đầu — dictionary không được phép ném lỗi ở
        // đây, mọi đường gọi tới cây này đều là đường phụ trợ fail-open.
        _byCode = units
            .GroupBy(u => u.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(u => u.Code, u => u, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Mọi đơn vị trong cây (kể cả department).</summary>
    public IReadOnlyCollection<OrgUnitNode> Units => _byCode.Values;

    /// <summary>Đơn vị mang đúng mã này; null khi mã rỗng hoặc không còn tồn tại trong dữ liệu HR.</summary>
    public OrgUnitNode? Find(string? orgUnitCode)
        => string.IsNullOrWhiteSpace(orgUnitCode) ? null
            : _byCode.TryGetValue(orgUnitCode.Trim(), out var unit) ? unit : null;

    /// <summary>
    /// Department GẦN NHẤT chứa đơn vị này: chính nó nếu nó đã là department, ngược lại đi ngược
    /// <c>TargetResponsible</c> cho tới đơn vị đầu tiên có <see cref="OrgUnitNode.IsDepartment"/>.
    /// Trả null khi mã không tồn tại HOẶC chuỗi cấp trên không dẫn tới department nào (dữ liệu HR đứt
    /// đoạn / trỏ ra ngoài) — caller tự quyết fallback, vì mỗi nơi cần một fallback khác nhau.
    /// </summary>
    public OrgUnitNode? FindDepartment(string? orgUnitCode)
    {
        var unit = Find(orgUnitCode);
        if (unit == null)
            return null;

        // visited chặn vòng lặp dữ liệu bẩn (TargetResponsible tự trỏ về mình hoặc tạo chu trình).
        var current = unit;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.Code };
        while (!current.IsDepartment
               && !string.IsNullOrWhiteSpace(current.ParentCode)
               && _byCode.TryGetValue(current.ParentCode!, out var parent)
               && visited.Add(parent.Code))
        {
            current = parent;
        }

        return current.IsDepartment ? current : null;
    }
}

/// <summary>
/// Đọc <see cref="OrgChart"/> từ DB, cache theo tiến trình — dữ liệu HR chỉ đổi khi đồng bộ lại, mà cây
/// này nay được hỏi ở mọi lượt chat (ghi chú đơn vị + bucket checklist) nên đọc lại mỗi lần là lãng phí.
/// </summary>
public class OrgChartProvider
{
    private const string CacheKey = "OrgChart.Units";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public OrgChartProvider(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<OrgChart> GetAsync(CancellationToken cancellationToken = default)
    {
        var chart = await _cache.GetOrCreateAsync(CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return LoadAsync(cancellationToken);
        });
        // GetOrCreateAsync trả nullable; cây rỗng vẫn là câu trả lời hợp lệ (bảng OrgUnits còn trống).
        return chart ?? new OrgChart(Array.Empty<OrgUnitNode>());
    }

    private async Task<OrgChart> LoadAsync(CancellationToken cancellationToken)
    {
        var rows = await _db.OrgUnits.AsNoTracking()
            .Where(u => !u.IsDelete && u.OrgUnitCode != null && u.OrgUnitCode != "")
            .Select(u => new { Code = u.OrgUnitCode!, u.DisplayName, u.TargetResponsible, u.TrgtManagerLId, u.IsDepartment })
            .ToListAsync(cancellationToken);

        return new OrgChart(rows.Select(u => new OrgUnitNode(
            u.Code,
            string.IsNullOrWhiteSpace(u.DisplayName) ? u.Code : u.DisplayName!,
            string.IsNullOrWhiteSpace(u.TargetResponsible) ? null : u.TargetResponsible!.Trim(),
            string.IsNullOrWhiteSpace(u.TrgtManagerLId) ? null : u.TrgtManagerLId!.Trim(),
            u.IsDepartment)));
    }
}
