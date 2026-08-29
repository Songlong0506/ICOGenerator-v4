using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Organization;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Kho "checklist học được" của BA — mỗi bài học là MỘT dòng <see cref="AgentChecklistItem"/>, gom theo
/// BUCKET: bucket CHUNG (<c>departmentCode = null</c>, áp dụng mọi dự án) và bucket THEO PHÒNG BAN (bài
/// học của phòng kho không làm nhiễu phỏng vấn của phòng nhân sự). Ba đường ghi
/// (<see cref="ChecklistGapMemoryService"/>, <see cref="PocFeedbackMemoryService"/>,
/// <see cref="SpecAssumptionMemoryService"/>) và đường nạp cho lượt chat đều đi qua đây để cùng một cách
/// chọn bucket — xem <see cref="ResolveBucketAsync"/>.
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
    private readonly OrgChartProvider _orgChart;

    public ChecklistNoteStore(AppDbContext db, OrgChartProvider orgChart)
    {
        _db = db;
        _orgChart = orgChart;
    }

    /// <summary>
    /// Bucket của một dự án, suy từ ĐƠN VỊ YÊU CẦU của nó: mã phòng ban chứa đơn vị đó, hoặc null (bucket
    /// chung) khi dự án chưa gắn đơn vị / mã không còn tồn tại / chuỗi cấp trên không dẫn tới department nào.
    ///
    /// <para>
    /// Vì sao rơi về bucket CHUNG chứ không lấy chính orgUnit làm bucket (khác trang Usage, nơi orgUnit lẻ
    /// vẫn thành một nhóm): dữ liệu HR có 195 orgUnit nhưng chỉ 15 department. Cho orgUnit tự làm bucket là
    /// mở đường cho hàng trăm bucket, mỗi bucket vài mục — dưới xa ngưỡng
    /// <see cref="MaxActiveItemsPerBucket"/> mà bộ nhớ này cần để có ích. Bảng Usage phải cộng đủ chi phí
    /// nên không được bỏ nhóm nào; bộ nhớ thì thà gộp vào bucket chung còn hơn vụn ra vô dụng.
    /// </para>
    /// </summary>
    public async Task<string?> ResolveBucketAsync(string? orgUnitCode, CancellationToken cancellationToken = default)
    {
        var chart = await _orgChart.GetAsync(cancellationToken);
        return chart.FindDepartment(orgUnitCode)?.Code;
    }

    /// <summary>
    /// Toàn bộ mục của MỘT bucket (cả mục đã tắt — vòng harvest cần chúng làm danh sách cấm), cũ trước
    /// mới sau. Entity được TRACK: <see cref="MergeHarvest"/> sửa trạng thái ngay trên chúng.
    /// </summary>
    public Task<List<AgentChecklistItem>> LoadBucketAsync(Agent ba, string? departmentCode, CancellationToken cancellationToken = default)
    {
        var bucket = Normalize(departmentCode);
        return _db.AgentChecklistItems
            .Where(x => x.AgentId == ba.Id && x.DepartmentCode == bucket)
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
        string? departmentCode,
        List<AgentChecklistItem> existing,
        IEnumerable<ChecklistLesson> lessons,
        ChecklistItemSource sourceKind,
        Guid? sourceProjectId)
    {
        var bucket = Normalize(departmentCode);
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
                DepartmentCode = bucket,
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
    /// Khối checklist nạp cho MỘT lượt chat: mục đang dùng của bucket chung + của bucket phòng ban của dự
    /// án (nếu giải được). Trả null khi cả hai đều trống — caller bỏ qua system message này như trước.
    /// </summary>
    public async Task<string?> BuildForChatAsync(Agent ba, string? departmentCode, CancellationToken cancellationToken = default)
    {
        var buckets = new List<string?> { null };
        if (!string.IsNullOrWhiteSpace(departmentCode))
            buckets.Add(Normalize(departmentCode));

        var active = await _db.AgentChecklistItems
            .AsNoTracking()
            .Where(x => x.AgentId == ba.Id && buckets.Contains(x.DepartmentCode) && x.Status == ChecklistItemStatus.Active)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var parts = new List<string>();

        var common = Render(active.Where(x => x.DepartmentCode == null));
        if (common.Length > 0)
            parts.Add(common);

        if (buckets.Count > 1)
        {
            var lines = Render(active.Where(x => x.DepartmentCode == buckets[1]));
            if (lines.Length > 0)
            {
                // Tiêu đề gọi TÊN phòng ban, không phải mã: "HcP/MFE2" nói với model nhiều hơn "50123".
                // Mã không tra được tên (dữ liệu HR đứt) thì dùng chính mã — vẫn tốt hơn bỏ trống.
                var chart = await _orgChart.GetAsync(cancellationToken);
                var name = chart.Find(buckets[1])?.DisplayName ?? buckets[1];
                parts.Add($"### Riêng cho phòng ban \"{name}\" (đơn vị yêu cầu của dự án hiện tại thuộc phòng này)\n{lines}");
            }
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

    // Bucket chung dùng NULL thống nhất ở mọi đường (query "không có phòng ban" và query bucket phòng ban
    // dùng chung một cột), nên chuỗi rỗng/space cũng phải quy về null.
    private static string? Normalize(string? departmentCode) =>
        string.IsNullOrWhiteSpace(departmentCode) ? null : departmentCode.Trim();

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
