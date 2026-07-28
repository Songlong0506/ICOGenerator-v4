using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

/// <summary>Một dự án đã đóng góp bài học vào một bucket checklist (để trang quản trị truy nguồn).</summary>
public record ChecklistSourceProject(Guid ProjectId, string Name, DateTime CreatedAt);

/// <summary>
/// Một bucket "checklist học được": bucket CHUNG (<see cref="DomainKey"/> = null) hoặc bucket của một
/// miền nghiệp vụ, kèm các dự án đã đóng góp vào nó.
/// </summary>
public record LearnedChecklistBucket(
    string? DomainKey,
    string Notes,
    DateTime? UpdatedAt,
    IReadOnlyList<ChecklistSourceProject> Sources)
{
    public int ItemCount => Notes.Replace("\r\n", "\n").Split('\n').Count(l => l.TrimStart().StartsWith("-", StringComparison.Ordinal));
}

/// <summary>
/// Trang quản trị "checklist học được" của BA.
///
/// <para>
/// Vì sao cần: hai đường harvest (<see cref="ChecklistGapMemoryService"/> từ hội thoại,
/// <see cref="PocFeedbackMemoryService"/> từ ghi chú POC) tự bồi bài học vào các bucket này, và mỗi lượt
/// chat của MỌI dự án sau đó đều nạp chúng vào prompt BA. Trước đây không có màn hình nào xem được nội
/// dung đó, nên một bài học rút sai từ một dự án cá biệt sẽ âm thầm làm nhiễu phỏng vấn của mọi dự án
/// cùng miền — không ai biết để gỡ.
/// </para>
///
/// <para>
/// Nguồn gốc mỗi bucket suy TRỰC TIẾP từ dữ liệu sẵn có (dự án cùng miền đã chạy harvest) chứ không cần
/// cột mới: đủ để trả lời câu hỏi thật sự quan trọng khi thấy một mục lạ — "bài học này đến từ dự án nào".
/// </para>
/// </summary>
public class GetLearnedChecklistQuery
{
    private readonly AppDbContext _db;
    private readonly BAAgentResolver _agentResolver;

    public GetLearnedChecklistQuery(AppDbContext db, BAAgentResolver agentResolver)
    {
        _db = db;
        _agentResolver = agentResolver;
    }

    public async Task<IReadOnlyList<LearnedChecklistBucket>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Không đòi model: trang này chỉ đọc/sửa dữ liệu, không gọi LLM.
        var ba = await _agentResolver.FindTrackedAsync(cancellationToken);
        if (ba == null)
            return Array.Empty<LearnedChecklistBucket>();

        // Dự án đã thật sự chạy harvest (rút từ hội thoại hoặc từ ghi chú POC) — nguồn của các bucket.
        var contributors = await _db.Projects.AsNoTracking()
            .Where(p => p.ChecklistGapHarvested || p.PocFeedbackHarvestedCount > 0)
            .Select(p => new ContributingProject(p.Id, p.Name, p.DomainKey, p.CreatedAt))
            .ToListAsync(cancellationToken);

        var domainRows = await _db.AgentDomainChecklistNotes.AsNoTracking()
            .Where(x => x.AgentId == ba.Id)
            .OrderBy(x => x.DomainKey)
            .ToListAsync(cancellationToken);

        var buckets = new List<LearnedChecklistBucket>();

        if (!string.IsNullOrWhiteSpace(ba.LearnedChecklistNotes))
        {
            // Bucket chung nhận bài học của dự án CHƯA phân loại miền.
            buckets.Add(new LearnedChecklistBucket(
                null,
                ba.LearnedChecklistNotes!.Trim(),
                null,
                SourcesOf(contributors.Where(p => string.IsNullOrWhiteSpace(p.DomainKey)))));
        }

        foreach (var row in domainRows)
        {
            buckets.Add(new LearnedChecklistBucket(
                row.DomainKey,
                row.Notes.Trim(),
                row.UpdatedAt,
                SourcesOf(contributors.Where(p => p.DomainKey == row.DomainKey))));
        }

        return buckets;

        static IReadOnlyList<ChecklistSourceProject> SourcesOf(IEnumerable<ContributingProject> projects) =>
            projects
                .OrderByDescending(p => p.CreatedAt)
                .Take(10)
                .Select(p => new ChecklistSourceProject(p.Id, p.Name, p.CreatedAt))
                .ToList();
    }

    private record ContributingProject(Guid Id, string Name, string? DomainKey, DateTime CreatedAt);
}

public enum UpdateLearnedChecklistResult { Ok, BaNotConfigured }

/// <summary>
/// Sửa/gỡ nội dung một bucket checklist học được. Gỡ = lưu chuỗi rỗng (store tự xóa dòng bucket) — đây
/// là van an toàn cho bài học rút sai: nó đang được nạp vào MỌI lượt chat của các dự án cùng miền.
/// </summary>
public class UpdateLearnedChecklistUseCase
{
    private readonly AppDbContext _db;
    private readonly ChecklistNoteStore _noteStore;
    private readonly BAAgentResolver _agentResolver;

    public UpdateLearnedChecklistUseCase(AppDbContext db, ChecklistNoteStore noteStore, BAAgentResolver agentResolver)
    {
        _db = db;
        _noteStore = noteStore;
        _agentResolver = agentResolver;
    }

    public async Task<UpdateLearnedChecklistResult> ExecuteAsync(string? domainKey, string? notes, CancellationToken cancellationToken = default)
    {
        var ba = await _agentResolver.FindTrackedAsync(cancellationToken);
        if (ba == null)
            return UpdateLearnedChecklistResult.BaNotConfigured;

        await _noteStore.SetBucketAsync(ba, domainKey, notes?.Trim(), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return UpdateLearnedChecklistResult.Ok;
    }
}
