using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chuyển "checklist học được" từ kho BLOB cũ (<see cref="Agent.LearnedChecklistNotes"/> và bảng
/// <see cref="AgentDomainChecklistNote"/>) sang từng dòng <see cref="AgentChecklistItem"/> — chạy MỘT
/// LẦN lúc khởi động (<see cref="Data.DbInitializer"/>) để các bản cài đã học được nhiều dự án không mất
/// dữ liệu khi lên bản này.
///
/// <para>
/// Idempotent theo nghĩa mạnh: nguồn được XÓA ngay sau khi nhập (cột về null, dòng bucket bị gỡ), nên
/// lần khởi động sau không còn gì để nhập. Mục nhập vào mang <see cref="ChecklistItemSource.Legacy"/> và
/// KHÔNG có lý do đi kèm — blob cũ chưa bao giờ lưu "vì sao rút ra bài học này", đó chính là thứ bản
/// này bắt đầu ghi lại từ vòng harvest kế tiếp.
/// </para>
/// </summary>
public class ChecklistLegacyNotesImporter
{
    private readonly AppDbContext _db;
    private readonly ILogger<ChecklistLegacyNotesImporter> _logger;

    public ChecklistLegacyNotesImporter(AppDbContext db, ILogger<ChecklistLegacyNotesImporter> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <returns>Số mục đã nhập (0 = không có gì để nhập).</returns>
    public async Task<int> ImportAsync(CancellationToken cancellationToken = default)
    {
        var imported = 0;

        var agents = await _db.Agents
            .Where(a => a.LearnedChecklistNotes != null && a.LearnedChecklistNotes != "")
            .ToListAsync(cancellationToken);
        foreach (var agent in agents)
        {
            imported += AddItems(agent.Id, null, agent.LearnedChecklistNotes);
            agent.LearnedChecklistNotes = null;
        }

        var domainRows = await _db.AgentDomainChecklistNotes.ToListAsync(cancellationToken);
        foreach (var row in domainRows)
        {
            imported += AddItems(row.AgentId, row.DomainKey, row.Notes);
            _db.AgentDomainChecklistNotes.Remove(row);
        }

        if (imported == 0 && domainRows.Count == 0 && agents.Count == 0)
            return 0;

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Imported {Count} legacy learned-checklist item(s) into AgentChecklistItems.", imported);
        return imported;
    }

    // Mỗi dòng gạch đầu dòng của blob = một mục. Dòng không phải gạch đầu dòng (tiêu đề, câu dẫn model
    // lỡ thêm) bị bỏ — chúng chưa bao giờ là một mục checklist thật.
    private int AddItems(Guid agentId, string? domainKey, string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return 0;

        var bucket = string.IsNullOrWhiteSpace(domainKey) ? null : domainKey.Trim();
        var added = 0;
        var createdAt = DateTime.UtcNow;

        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("- ", StringComparison.Ordinal) && !line.StartsWith("* ", StringComparison.Ordinal))
                continue;

            var text = line[2..].Trim();
            if (text.Length == 0)
                continue;

            _db.AgentChecklistItems.Add(new AgentChecklistItem
            {
                AgentId = agentId,
                DomainKey = bucket,
                Text = text.Length > 400 ? text[..400] : text,
                SourceKind = ChecklistItemSource.Legacy,
                // Giữ nguyên thứ tự cũ của blob: mỗi mục lệch nhau một tick để sắp xếp theo CreatedAt ổn định.
                CreatedAt = createdAt.AddMilliseconds(added),
                UpdatedAt = createdAt.AddMilliseconds(added)
            });
            added++;
        }

        return added;
    }
}
