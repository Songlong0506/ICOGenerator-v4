using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Kho "checklist học được" của BA, tách theo BUCKET miền nghiệp vụ: bucket CHUNG (bài học áp dụng mọi
/// miền — cột <see cref="Agent.LearnedChecklistNotes"/>, giữ nguyên vị trí cũ để dữ liệu đã học không
/// mất) và bucket THEO MIỀN (bảng <see cref="AgentDomainChecklistNote"/>, mỗi (agent, miền) một dòng).
/// Hai đường ghi (ChecklistGapMemoryService, PocFeedbackMemoryService) và đường nạp cho lượt chat đều
/// đi qua đây để cùng một cách chọn bucket: dự án ĐÃ có DomainKey ⇒ bài học vào bucket miền đó; chưa
/// phân loại ⇒ bucket chung.
/// </summary>
public class ChecklistNoteStore
{
    private readonly AppDbContext _db;

    public ChecklistNoteStore(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Notes hiện có của MỘT bucket (domainKey null/rỗng/"other" không tách bucket riêng thì vẫn là bucket đó — "other" cũng là một bucket hợp lệ).</summary>
    public async Task<string?> LoadBucketAsync(Agent ba, string? domainKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(domainKey))
            return ba.LearnedChecklistNotes;

        var row = await _db.AgentDomainChecklistNotes
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AgentId == ba.Id && x.DomainKey == domainKey, cancellationToken);
        return row?.Notes;
    }

    /// <summary>
    /// Ghi notes vào đúng bucket. KHÔNG SaveChanges — caller (vòng harvest) tự lưu cùng các thay đổi
    /// khác của nó (con trỏ harvest, cờ đã-rà-soát) để giữ nguyên tính nguyên tử như trước.
    /// </summary>
    public async Task SetBucketAsync(Agent ba, string? domainKey, string? notes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(domainKey))
        {
            ba.LearnedChecklistNotes = string.IsNullOrWhiteSpace(notes) ? null : notes;
            return;
        }

        var row = await _db.AgentDomainChecklistNotes
            .FirstOrDefaultAsync(x => x.AgentId == ba.Id && x.DomainKey == domainKey, cancellationToken);

        if (string.IsNullOrWhiteSpace(notes))
        {
            if (row != null)
                _db.AgentDomainChecklistNotes.Remove(row);
            return;
        }

        if (row == null)
        {
            _db.AgentDomainChecklistNotes.Add(new AgentDomainChecklistNote
            {
                AgentId = ba.Id,
                DomainKey = domainKey,
                Notes = notes
            });
        }
        else
        {
            row.Notes = notes;
            row.UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Khối checklist nạp cho MỘT lượt chat: bucket chung + bucket đúng miền của dự án (nếu đã phân
    /// loại). Trả null khi cả hai đều trống — caller bỏ qua system message này như trước.
    /// </summary>
    public async Task<string?> BuildForChatAsync(Agent ba, string? domainKey, CancellationToken cancellationToken = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ba.LearnedChecklistNotes))
            parts.Add(ba.LearnedChecklistNotes.Trim());

        if (!string.IsNullOrWhiteSpace(domainKey))
        {
            var domainNotes = await LoadBucketAsync(ba, domainKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(domainNotes))
                parts.Add($"### Riêng cho miền nghiệp vụ \"{domainKey}\" (dự án hiện tại thuộc miền này)\n{domainNotes.Trim()}");
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }
}
