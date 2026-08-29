using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Organization;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

/// <summary>Một dự án đã đóng góp bài học vào một bucket checklist (để trang quản trị truy nguồn).</summary>
public record ChecklistSourceProject(Guid ProjectId, string Name);

/// <summary>
/// Một bài học trong checklist học được, kèm ĐÚNG những gì cần để người quản trị phán đoán nó đúng hay
/// sai: nội dung, vì sao rút ra, bằng chứng gốc, dự án nguồn, và đang bật hay tắt.
/// </summary>
public record LearnedChecklistItemVm(
    Guid Id,
    string Text,
    string? Rationale,
    string? Evidence,
    ChecklistItemSource SourceKind,
    Guid? SourceProjectId,
    string? SourceProjectName,
    ChecklistItemStatus Status,
    DateTime CreatedAt)
{
    public bool IsActive => Status == ChecklistItemStatus.Active;

    /// <summary>Mới học trong 7 ngày gần đây — để người quản trị soi trước những bài học chưa ai kịp xem.</summary>
    public bool IsRecent => CreatedAt >= DateTime.UtcNow.AddDays(-7);
}

/// <summary>
/// Một bucket "checklist học được": bucket CHUNG (<see cref="DepartmentCode"/> = null) hoặc bucket của
/// một phòng ban, cùng các bài học của nó. <see cref="DepartmentName"/> là tên phòng tra từ OrgUnits để
/// hiển thị — mã trần ("50123") không nói gì với người đọc trang này; null khi mã không còn tra được.
/// </summary>
public record LearnedChecklistBucket(
    string? DepartmentCode,
    string? DepartmentName,
    IReadOnlyList<LearnedChecklistItemVm> Items,
    DateTime? UpdatedAt)
{
    /// <summary>Nhãn hiển thị của bucket: tên phòng nếu tra được, ngược lại chính mã.</summary>
    public string? DepartmentLabel => DepartmentName ?? DepartmentCode;

    public int ActiveCount => Items.Count(i => i.IsActive);

    /// <summary>Dự án nào đã đóng góp bài học vào bucket này — suy từ chính nguồn của từng mục.</summary>
    public IReadOnlyList<ChecklistSourceProject> Sources => Items
        .Where(i => i.SourceProjectId != null && i.SourceProjectName != null)
        .GroupBy(i => i.SourceProjectId!.Value)
        .Select(g => new ChecklistSourceProject(g.Key, g.First().SourceProjectName!))
        .ToList();
}

/// <summary>
/// Trang quản trị "checklist học được" của BA.
///
/// <para>
/// Vì sao cần: hai đường harvest (<see cref="ChecklistGapMemoryService"/> từ hội thoại,
/// <see cref="PocFeedbackMemoryService"/> từ ghi chú POC) tự bồi bài học vào các bucket này, và mỗi lượt
/// chat của MỌI dự án sau đó đều nạp chúng vào prompt BA. Không có màn hình này thì một bài học rút sai
/// từ một dự án cá biệt sẽ âm thầm làm nhiễu phỏng vấn của mọi dự án cùng phòng ban — không ai biết để gỡ.
/// </para>
///
/// <para>
/// Mỗi mục mang theo LÝ DO rút ra + trích dẫn bằng chứng + dự án nguồn: "cái này ở đâu ra" là câu hỏi
/// đầu tiên khi thấy một mục lạ, và người quản trị không có cách nào khác để trả lời.
/// </para>
/// </summary>
public class GetLearnedChecklistQuery
{
    private readonly AppDbContext _db;
    private readonly BAAgentResolver _agentResolver;
    private readonly OrgChartProvider _orgChart;

    public GetLearnedChecklistQuery(AppDbContext db, BAAgentResolver agentResolver, OrgChartProvider orgChart)
    {
        _db = db;
        _agentResolver = agentResolver;
        _orgChart = orgChart;
    }

    public async Task<IReadOnlyList<LearnedChecklistBucket>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Không đòi model: trang này chỉ đọc/sửa dữ liệu, không gọi LLM.
        var ba = await _agentResolver.FindTrackedAsync(cancellationToken);
        if (ba == null)
            return Array.Empty<LearnedChecklistBucket>();

        var items = await _db.AgentChecklistItems.AsNoTracking()
            .Where(x => x.AgentId == ba.Id)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        // Tên dự án nguồn tra riêng (một query) thay vì join: khóa nullable + dự án đã xóa (FK SetNull)
        // làm join dễ nuốt mất chính những mục không truy được nguồn — vốn là mục cần soi nhất.
        var sourceIds = items.Where(x => x.SourceProjectId != null).Select(x => x.SourceProjectId!.Value).Distinct().ToList();
        var projectNames = sourceIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Projects.AsNoTracking()
                .Where(p => sourceIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        var chart = await _orgChart.GetAsync(cancellationToken);

        return items
            .GroupBy(x => x.DepartmentCode)
            .OrderBy(g => g.Key == null ? 0 : 1)
            .ThenBy(g => g.Key)
            .Select(g => new LearnedChecklistBucket(
                g.Key,
                chart.Find(g.Key)?.DisplayName,
                g.Select(x => new LearnedChecklistItemVm(
                    x.Id,
                    x.Text,
                    x.Rationale,
                    x.Evidence,
                    x.SourceKind,
                    x.SourceProjectId,
                    x.SourceProjectId != null && projectNames.TryGetValue(x.SourceProjectId.Value, out var name) ? name : null,
                    x.Status,
                    x.CreatedAt)).ToList(),
                g.Max(x => x.UpdatedAt)))
            .ToList();
    }
}

/// <summary>Một dòng người dùng gửi lên từ form checklist (tick bật/tắt + nội dung đã sửa tại chỗ).</summary>
public class ChecklistItemInput
{
    public Guid Id { get; set; }
    public string? Text { get; set; }
    public bool Enabled { get; set; }
}

public enum SaveLearnedChecklistResult { Ok, BaNotConfigured }

/// <summary>
/// Ghi lại thao tác của người quản trị trên một bucket checklist: bật/tắt từng bài học, sửa lời văn của
/// mục, xóa hẳn một mục, hoặc tắt cả bucket.
///
/// <para>
/// TẮT (giữ mục lại) chứ không xóa là mặc định có chủ ý: mục đã tắt vừa bật lại được, vừa được gửi cho
/// vòng harvest sau như danh sách cấm — trước đây xóa chữ trong ô text thì dự án sau lộ lại đúng khoảng
/// trống là bài học sai quay về y hệt. Xóa hẳn chỉ dành cho mục rác thật sự.
/// </para>
/// </summary>
public class SaveLearnedChecklistUseCase
{
    private readonly AppDbContext _db;
    private readonly BAAgentResolver _agentResolver;

    public SaveLearnedChecklistUseCase(AppDbContext db, BAAgentResolver agentResolver)
    {
        _db = db;
        _agentResolver = agentResolver;
    }

    /// <summary>Áp trạng thái bật/tắt và lời văn người dùng gửi lên cho các mục của MỘT bucket.</summary>
    public async Task<SaveLearnedChecklistResult> SaveAsync(string? departmentCode, IReadOnlyList<ChecklistItemInput> inputs, CancellationToken cancellationToken = default)
    {
        var items = await LoadBucketAsync(departmentCode, cancellationToken);
        if (items == null)
            return SaveLearnedChecklistResult.BaNotConfigured;

        var byId = items.ToDictionary(x => x.Id);
        foreach (var input in inputs)
        {
            if (!byId.TryGetValue(input.Id, out var item))
                continue; // mục đã bị xóa ở tab khác — bỏ qua thay vì dựng lại.

            var text = (input.Text ?? string.Empty).Trim();
            if (text.Length > 0 && text != item.Text)
            {
                item.Text = text.Length > 400 ? text[..400] : text;
                item.UpdatedAt = DateTime.UtcNow;
            }

            var status = input.Enabled ? ChecklistItemStatus.Active : ChecklistItemStatus.DisabledByUser;
            if (status != item.Status)
            {
                item.Status = status;
                item.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return SaveLearnedChecklistResult.Ok;
    }

    /// <summary>Tắt mọi bài học của một bucket — BA thôi hỏi cả nhóm, nhưng vẫn xem lại/bật lại được.</summary>
    public async Task<SaveLearnedChecklistResult> DisableBucketAsync(string? departmentCode, CancellationToken cancellationToken = default)
    {
        var items = await LoadBucketAsync(departmentCode, cancellationToken);
        if (items == null)
            return SaveLearnedChecklistResult.BaNotConfigured;

        foreach (var item in items.Where(x => x.Status == ChecklistItemStatus.Active))
        {
            item.Status = ChecklistItemStatus.DisabledByUser;
            item.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return SaveLearnedChecklistResult.Ok;
    }

    /// <summary>Xóa hẳn một mục. Lưu ý: mục biến mất khỏi danh sách cấm nên vòng harvest sau CÓ THỂ học lại.</summary>
    public async Task<SaveLearnedChecklistResult> DeleteAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var ba = await _agentResolver.FindTrackedAsync(cancellationToken);
        if (ba == null)
            return SaveLearnedChecklistResult.BaNotConfigured;

        var item = await _db.AgentChecklistItems.FirstOrDefaultAsync(x => x.Id == itemId && x.AgentId == ba.Id, cancellationToken);
        if (item != null)
        {
            _db.AgentChecklistItems.Remove(item);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return SaveLearnedChecklistResult.Ok;
    }

    // null = chưa cấu hình agent BA (không phải "bucket rỗng").
    private async Task<List<Domain.AgentChecklistItem>?> LoadBucketAsync(string? departmentCode, CancellationToken cancellationToken)
    {
        var ba = await _agentResolver.FindTrackedAsync(cancellationToken);
        if (ba == null)
            return null;

        var bucket = string.IsNullOrWhiteSpace(departmentCode) ? null : departmentCode.Trim();
        return await _db.AgentChecklistItems
            .Where(x => x.AgentId == ba.Id && x.DepartmentCode == bucket)
            .ToListAsync(cancellationToken);
    }
}
