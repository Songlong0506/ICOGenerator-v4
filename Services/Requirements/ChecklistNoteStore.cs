using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Kho "checklist học được" của BA — mỗi bài học là MỘT dòng <see cref="AgentChecklistItem"/>, gom theo
/// BUCKET: bucket CHUNG (<c>domainKey = null</c>, áp dụng mọi dự án) và bucket THEO MIỀN nghiệp vụ (bài
/// học của dự án kho không làm nhiễu phỏng vấn dự án nghỉ phép). Hai đường ghi
/// (<see cref="ChecklistGapMemoryService"/>, <see cref="PocFeedbackMemoryService"/>) và đường nạp cho
/// lượt chat đều đi qua đây để cùng một cách chọn bucket: dự án ĐÃ có DomainKey ⇒ bucket miền đó; chưa
/// phân loại ⇒ bucket chung.
///
/// <para>
/// Hai luật sống còn của kho này:
/// <list type="number">
///   <item><b>Chỉ <see cref="AgentChecklistItem.Text"/> đi vào prompt.</b> Lý do/bằng chứng của mỗi mục
///         chỉ phục vụ trang quản trị — nhồi chúng vào prompt là bắt MỌI lượt chat của mọi dự án cùng
///         bucket trả token cho phần chỉ con người đọc.</item>
///   <item><b>Bài học người dùng đã tắt không bao giờ được học lại.</b> Vòng harvest nhận cả mục đã tắt
///         làm danh sách cấm, và <see cref="MergeHarvest"/> chặn trùng lần nữa ở tầng dữ liệu — trước
///         đây xóa một mục sai trong blob thì dự án sau lộ lại đúng khoảng trống là nó quay về y hệt.</item>
/// </list>
/// </para>
/// </summary>
public class ChecklistNoteStore
{
    /// <summary>
    /// Trần số mục ĐANG DÙNG của một bucket. Vượt trần thì mục cũ nhất tự chuyển
    /// <see cref="ChecklistItemStatus.DisabledByOverflow"/> (vẫn hiện trên trang quản trị, bật lại được)
    /// — thay cho việc blob cũ nhờ LLM "tự bỏ bớt mục ít giá trị", vốn âm thầm và không hoàn tác được.
    /// </summary>
    public const int MaxActiveItemsPerBucket = 25;

    /// <summary>Trần số bài học nhận từ MỘT vòng harvest — một dự án không thể đổ ập cả chục mục vào bộ nhớ chung.</summary>
    public const int MaxLessonsPerHarvest = 5;

    // Trần ký tự phần checklist của MỘT bucket khi nạp vào prompt — giữ đúng ngân sách của blob cũ
    // (MaxNotesChars = 4000), nhưng cắt theo RANH GIỚI MỤC thay vì cắt giữa câu như trước.
    private const int MaxCharsPerBucketInPrompt = 4000;

    private readonly AppDbContext _db;

    public ChecklistNoteStore(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Toàn bộ mục của MỘT bucket (cả mục đã tắt — vòng harvest cần chúng làm danh sách cấm), cũ trước
    /// mới sau. Entity được TRACK: <see cref="MergeHarvest"/> sửa trạng thái ngay trên chúng.
    /// </summary>
    public Task<List<AgentChecklistItem>> LoadBucketAsync(Agent ba, string? domainKey, CancellationToken cancellationToken = default)
    {
        var bucket = Normalize(domainKey);
        return _db.AgentChecklistItems
            .Where(x => x.AgentId == ba.Id && x.DomainKey == bucket)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Thêm các bài học MỚI rút được vào bucket. KHÔNG SaveChanges — caller (vòng harvest) tự lưu cùng
    /// các thay đổi khác của nó (con trỏ harvest, cờ đã-rà-soát) để giữ nguyên tính nguyên tử.
    /// <para>
    /// Bỏ qua bài học trùng nội dung với một mục ĐÃ CÓ trong bucket, kể cả mục đang tắt — đây là chỗ
    /// bảo đảm "người dùng tắt rồi thì không học lại", không phụ thuộc việc LLM có tuân thủ hay không.
    /// </para>
    /// </summary>
    /// <param name="existing">Kết quả <see cref="LoadBucketAsync"/> của chính bucket này (đang track).</param>
    /// <returns>Số mục thật sự được thêm.</returns>
    public int MergeHarvest(
        Agent ba,
        string? domainKey,
        List<AgentChecklistItem> existing,
        IEnumerable<ChecklistLesson> lessons,
        ChecklistItemSource sourceKind,
        Guid? sourceProjectId)
    {
        var bucket = Normalize(domainKey);
        var seen = existing.Select(x => Fingerprint(x.Text)).ToHashSet(StringComparer.Ordinal);
        var added = 0;

        foreach (var lesson in lessons.Take(MaxLessonsPerHarvest))
        {
            var text = CleanLine(lesson.Text);
            if (text.Length == 0)
                continue;

            var fingerprint = Fingerprint(text);
            if (!seen.Add(fingerprint))
                continue; // trùng mục đang dùng HOẶC mục người dùng đã tắt ⇒ không học lại.

            var item = new AgentChecklistItem
            {
                AgentId = ba.Id,
                DomainKey = bucket,
                Text = text.Length <= 400 ? text : text[..400],
                Rationale = Clamp(Clean(lesson.Rationale), 600),
                Evidence = Clamp(Clean(lesson.Evidence), 600),
                SourceKind = sourceKind,
                SourceProjectId = sourceProjectId
            };
            _db.AgentChecklistItems.Add(item);
            existing.Add(item);
            added++;
        }

        if (added > 0)
            ApplyOverflow(existing);

        return added;
    }

    /// <summary>
    /// Khối checklist nạp cho MỘT lượt chat: mục đang dùng của bucket chung + của bucket đúng miền dự án
    /// (nếu đã phân loại). Trả null khi cả hai đều trống — caller bỏ qua system message này như trước.
    /// </summary>
    public async Task<string?> BuildForChatAsync(Agent ba, string? domainKey, CancellationToken cancellationToken = default)
    {
        var buckets = new List<string?> { null };
        if (!string.IsNullOrWhiteSpace(domainKey))
            buckets.Add(Normalize(domainKey));

        var active = await _db.AgentChecklistItems
            .AsNoTracking()
            .Where(x => x.AgentId == ba.Id && buckets.Contains(x.DomainKey) && x.Status == ChecklistItemStatus.Active)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var parts = new List<string>();

        var common = Render(active.Where(x => x.DomainKey == null));
        if (common.Length > 0)
            parts.Add(common);

        if (buckets.Count > 1)
        {
            var domain = Render(active.Where(x => x.DomainKey == buckets[1]));
            if (domain.Length > 0)
                parts.Add($"### Riêng cho miền nghiệp vụ \"{buckets[1]}\" (dự án hiện tại thuộc miền này)\n{domain}");
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>
    /// Ngữ cảnh checklist gửi kèm cho vòng harvest: mục ĐANG DÙNG (để không đề xuất trùng) và mục ĐÃ BỊ
    /// LOẠI (để không học lại đúng bài học người dùng vừa tắt — thứ mà blob cũ không cách nào diễn đạt).
    /// </summary>
    public static string RenderContextForHarvest(IEnumerable<AgentChecklistItem> items)
    {
        var sb = new StringBuilder();

        var active = items.Where(x => x.Status == ChecklistItemStatus.Active).ToList();
        if (active.Count > 0)
        {
            sb.AppendLine("## Checklist đang dùng (KHÔNG đề xuất lại các ý này)");
            foreach (var item in active)
                sb.AppendLine($"- {item.Text}");
            sb.AppendLine();
        }

        var disabled = items.Where(x => x.Status != ChecklistItemStatus.Active).ToList();
        if (disabled.Count > 0)
        {
            sb.AppendLine("## Bài học đã bị loại (người dùng đã tắt — TUYỆT ĐỐI không đề xuất lại)");
            foreach (var item in disabled)
                sb.AppendLine($"- {item.Text}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // Vượt trần ⇒ tắt bớt mục ĐANG DÙNG cũ nhất cho tới khi vừa trần. Không đụng mục người dùng đã tự
    // tắt (chúng đã ở ngoài prompt) và không xóa gì — trang quản trị vẫn thấy, bật lại được.
    private static void ApplyOverflow(List<AgentChecklistItem> bucketItems)
    {
        var active = bucketItems
            .Where(x => x.Status == ChecklistItemStatus.Active)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToList();

        for (var i = 0; i < active.Count - MaxActiveItemsPerBucket; i++)
        {
            active[i].Status = ChecklistItemStatus.DisabledByOverflow;
            active[i].UpdatedAt = DateTime.UtcNow;
        }
    }

    // Nối các mục thành danh sách gạch đầu dòng, dừng TRƯỚC mục làm vượt trần ký tự (không cắt giữa câu).
    private static string Render(IEnumerable<AgentChecklistItem> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            var line = $"- {item.Text}";
            if (sb.Length + line.Length + 1 > MaxCharsPerBucketInPrompt)
                break;
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    // Bucket chung dùng NULL thống nhất ở mọi đường (query "chưa phân loại" và query bucket miền dùng
    // chung một cột), nên chuỗi rỗng/space cũng phải quy về null.
    private static string? Normalize(string? domainKey) =>
        string.IsNullOrWhiteSpace(domainKey) ? null : domainKey.Trim();

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Model hay trả kèm dấu gạch đầu dòng của định dạng cũ — bỏ để Text là nội dung thuần.
    private static string CleanLine(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
            text = text[2..].Trim();
        return text;
    }

    private static string? Clamp(string? value, int max) =>
        value == null || value.Length <= max ? value : value[..max];

    /// <summary>
    /// Khóa so trùng của một mục: bỏ hoa/thường, dấu câu và khoảng trắng thừa. Đủ để bắt trường hợp
    /// model diễn đạt lại gần y hệt bài học vừa bị tắt (không tham vọng bắt trùng ngữ nghĩa).
    /// </summary>
    private static string Fingerprint(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }
        return sb.ToString().TrimEnd();
    }
}
