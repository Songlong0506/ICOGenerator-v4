using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng đối tượng nghiệp vụ" — các thứ có hồ sơ riêng trong ứng dụng, thông tin cần lưu
/// về chúng, và vòng đời trạng thái kèm người nhận thông báo ở mỗi chuyển trạng thái (xem
/// <see cref="EntityMapRow"/>).
///
/// <para>
/// Ba chốt chặn tất định, và cả ba đều nhắm vào cùng một rủi ro: đây là bảng DỄ ĐỌC LƯỚT NHẤT trong bốn
/// bảng, vì nó dài nhất và gần với từ vựng kỹ thuật nhất.
/// </para>
/// <list type="bullet">
///   <item><b>Đối tượng rỗng ruột.</b> Một dòng không có thông tin nào cần lưu và cũng không có trạng thái
///   nào thì không phải đối tượng nghiệp vụ — nó là một danh từ model nhặt trong hội thoại. Bị loại.</item>
///   <item><b>Vòng đời một trạng thái.</b> "Vòng đời" chỉ có một trạng thái là không có vòng đời; giữ lại
///   là bày ra một bảng con mời người dùng xác nhận một điều vô nghĩa. Cắt sạch (đối tượng vẫn giữ, nó
///   chỉ là đối tượng danh mục).</item>
///   <item><b>Hỏi lại thứ bảng cột đã chốt.</b> Thông tin trùng tên một CỘT ĐÃ TÍCH của tài liệu nguồn được
///   đánh dấu là đã có bằng chứng — xem <see cref="Build"/>. Bắt người dùng duyệt lại đúng thứ họ vừa tự
///   tay tích là hình dạng vòng lặp câu hỏi chết mà repo đã phải dựng lưới một lần.</item>
/// </list>
///
/// <para>
/// Hai chốt chặn đầu nhắm vào MODEL, nên cả hai đều NHƯỜNG ở đường GỬI: người dùng thêm/xóa được dòng ngay
/// trên bảng (nút "+ thêm đối tượng", "+ thêm thông tin", "+ thêm trạng thái"), và một dòng họ vừa tự gõ mà
/// biến mất lúc lưu là đúng loại quyết định câm mà cả bảng này sinh ra để chặn. Xem
/// <see cref="EntityMapRow.AddedByUser"/> và <see cref="Sanitize"/>.
/// </para>
/// </summary>
public static class EntityMapBuilder
{
    /// <summary>Trần số đối tượng. Nhiều hơn thì bảng không rà nổi trong một lượt.</summary>
    public const int MaxRows = 12;

    /// <summary>Trần số thông tin của MỘT đối tượng.</summary>
    public const int MaxFieldsPerEntity = 12;

    /// <summary>Trần số trạng thái của MỘT đối tượng.</summary>
    public const int MaxStatesPerEntity = 8;

    /// <summary>Ít hơn ngần này trạng thái thì không phải vòng đời — xem ghi chú class.</summary>
    public const int MinStatesForLifecycle = 2;

    private const int MaxTextChars = 200;
    private const int MaxEvidenceChars = 300;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Bảng cuối cùng cho lượt BA BÀY BẢNG. <paramref name="confirmedColumns"/> là các cột ĐÃ TÍCH của mọi
    /// bảng cột người dùng đã chốt: thông tin trùng tên với chúng được khóa lại kèm trích dẫn "cột … của
    /// file …", vì họ đã trả lời câu đó rồi — chỉ khác là trả lời bằng cách tích chứ không gõ.
    ///
    /// <para>
    /// Mọi dòng và mọi thông tin ra khỏi đây đều TÍCH SẴN bất kể model trả gì: cờ tích là chỗ NGƯỜI DÙNG
    /// loại bớt, không phải chỗ model tự phủ nhận đề xuất của mình — mà structured output buộc điền đủ
    /// trường nên một model điền <c>false</c> cho có sẽ bỏ tích sạch bảng.
    /// </para>
    /// </summary>
    public static List<EntityMapRow> Build(IEnumerable<EntityMapRow>? proposed, IReadOnlyList<string>? confirmedColumns = null)
        => BuildCore(proposed, confirmedColumns, respectSelection: false);

    /// <summary>
    /// Bản chuẩn hoá cho dữ liệu ĐẾN TỪ TRÌNH DUYỆT: giữ đúng lựa chọn tích/bỏ tích của người dùng, xoá cờ
    /// khóa (bảng đã gửi thì mọi dòng là quyết định của họ), còn lại áp cùng luật với <see cref="Build"/>.
    ///
    /// <para>
    /// Khác <see cref="Build"/> ở hai chỗ nữa, và cả hai là chốt chặn nhắm vào model phải nhường cho NGƯỜI
    /// DÙNG: dòng mang cờ <see cref="EntityMapRow.AddedByUser"/> đi qua được cả khi nó chưa có thông tin hay
    /// trạng thái nào, và vòng đời MỘT trạng thái không bị cắt. Xem <see cref="NormalizeStates"/>.
    /// </para>
    /// </summary>
    public static List<EntityMapRow> Sanitize(IEnumerable<EntityMapRow>? submitted)
    {
        var rows = BuildCore(submitted, confirmedColumns: null, respectSelection: true);
        foreach (var row in rows)
        {
            // Cờ khóa bị xoá, cờ TỰ THÊM thì không: cái trước là lời khai của model về hội thoại (hết ý
            // nghĩa khi người dùng đã tự tay duyệt), cái sau là một sự thật về nguồn gốc của dòng mà
            // RenderUserMessage còn phải kể lại. Cùng luật với ScreenScopeMapBuilder.Sanitize.
            row.Locked = false;
            row.Evidence = string.Empty;
        }
        return rows;
    }

    private static List<EntityMapRow> BuildCore(
        IEnumerable<EntityMapRow>? proposed,
        IReadOnlyList<string>? confirmedColumns,
        bool respectSelection)
    {
        var columnKeys = (confirmedColumns ?? Array.Empty<string>())
            .Select(Normalize)
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<EntityMapRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in proposed ?? Enumerable.Empty<EntityMapRow>())
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Entity))
                continue;

            var entity = Clip(row.Entity.Trim(), MaxTextChars);
            if (!seen.Add(Normalize(entity)))
                continue;

            // Cờ tự thêm chỉ có nghĩa ở đường GỬI — xem EntityMapRow.AddedByUser.
            var addedByUser = respectSelection && row.AddedByUser;

            var fields = NormalizeFields(row.Fields, columnKeys, respectSelection);
            var states = NormalizeStates(row.States, respectSelection);
            // Không thông tin nào, không trạng thái nào ⇒ đây là một danh từ model nhặt được, không phải
            // đối tượng nghiệp vụ. Bày nó ra là mời người dùng xác nhận một dòng rỗng. Dòng NGƯỜI DÙNG tự
            // thêm thì ngược lại: họ vừa gõ tên nó vào bảng, tức là đã nói "ứng dụng còn phải lưu thứ này"
            // — nuốt nó đi là bắt họ gõ lại vào khung chat đúng thứ vừa gõ trên bảng, và BA sẽ hỏi tiếp
            // xem đối tượng đó cần lưu gì.
            if (fields.Count == 0 && states.Count == 0 && !addedByUser)
                continue;

            var evidence = Clip((row.Evidence ?? string.Empty).Trim(), MaxEvidenceChars);
            result.Add(new EntityMapRow
            {
                Entity = entity,
                Description = Clip((row.Description ?? string.Empty).Trim(), MaxTextChars),
                Fields = fields,
                States = states,
                Included = !respectSelection || row.Included,
                // LUẬT BẰNG CHỨNG — cờ suông không khóa được dòng nào. Xem PermissionGrant.Locked.
                Locked = evidence.Length > 0,
                Evidence = evidence,
                AddedByUser = addedByUser
            });

            if (result.Count >= MaxRows)
                break;
        }

        return result;
    }

    /// <summary>Đọc JSON bảng đối tượng đã lưu (cột DB hoặc payload client). null/rỗng/hỏng ⇒ mảng rỗng.</summary>
    public static List<EntityMapRow> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<EntityMapRow>();

        try
        {
            var rows = JsonSerializer.Deserialize<List<EntityMapRow>>(json, ReadOptions) ?? new List<EntityMapRow>();
            return rows.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Entity)).ToList();
        }
        catch
        {
            return new List<EntityMapRow>();
        }
    }

    /// <summary>Dự án này đã chốt bảng đối tượng chưa.</summary>
    public static bool IsConfirmed(string? json) => Parse(json).Count > 0;

    /// <summary>
    /// Khối ngữ cảnh gắn vào MỌI lượt chat sau khi bảng đã chốt, vào lượt distill bản đồ bao phủ, và vào
    /// prompt sinh AI Design Spec (mục <c>## 8. Data Model Summary</c>). Trả null khi chưa chốt.
    /// </summary>
    public static string? RenderConfirmedBlock(string? json)
    {
        var rows = Parse(json).Where(r => r.Included).ToList();
        if (rows.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng đối tượng nghiệp vụ đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại) ---");
        sb.AppendLine("Mỗi đối tượng: thông tin cần lưu, rồi vòng đời trạng thái kèm ĐIỀU KIỆN chuyển và AI ĐƯỢC "
            + "BÁO. Ô \"báo\" để trống nghĩa là KHÔNG gửi thông báo cho ai ở chuyển trạng thái đó — đó là quyết "
            + "định của người dùng, không phải chỗ còn thiếu.");

        foreach (var row in rows)
        {
            var description = string.IsNullOrWhiteSpace(row.Description) ? string.Empty : $" — {row.Description}";
            sb.AppendLine($"* {row.Entity}{description}");

            var fields = row.Fields.Where(f => f.Used).ToList();
            if (fields.Count > 0)
                sb.AppendLine("  - thông tin: " + string.Join("; ", fields.Select(RenderField)));

            foreach (var state in row.States)
                sb.AppendLine("  - trạng thái " + RenderState(state));

            // Đối tượng người dùng TỰ THÊM mà chưa điền gì: khối này đứng dưới lệnh "đừng hỏi lại", nên
            // không nói ra thì đối tượng ấy vĩnh viễn không ai hỏi cần lưu thông tin gì — đúng cái lỗ mà
            // bảng sinh ra để bịt, chỉ khác là lần này do chính người dùng mở ra. Ngoại lệ của "đừng hỏi
            // lại" phải nằm ngay tại dòng của nó, không phải trong một câu dặn chung ở đầu khối.
            if (fields.Count == 0 && row.States.Count == 0)
                sb.AppendLine("  - người dùng tự thêm đối tượng này và CHƯA nêu thông tin cần lưu ⇒ hỏi họ "
                    + "đối tượng này cần lưu những gì (đây là ngoại lệ duy nhất của luật \"đừng hỏi lại\").");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Tin nhắn mà TRÌNH DUYỆT gửi tiếp vào khung chat sau khi bảng đã lưu — cùng khuôn hai bước với các
    /// bảng khác, và soạn ở server vì cùng lý do: bản kể phải khớp đúng bản đã lưu.
    /// </summary>
    public static string RenderUserMessage(IReadOnlyList<EntityMapRow> rows)
    {
        if (rows.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Mình đã rà bảng đối tượng nghiệp vụ:");

        foreach (var row in rows.Where(r => r.Included))
        {
            sb.AppendLine();
            var description = string.IsNullOrWhiteSpace(row.Description) ? string.Empty : $" — {row.Description}";
            sb.AppendLine($"{row.Entity}{description}:");

            var fields = row.Fields.Where(f => f.Used).ToList();
            if (fields.Count > 0)
                sb.AppendLine("- thông tin cần lưu: " + string.Join("; ", fields.Select(RenderField)));

            var unused = row.Fields.Where(f => !f.Used && !string.IsNullOrWhiteSpace(f.Name)).ToList();
            if (unused.Count > 0)
                sb.AppendLine("- không cần lưu: " + string.Join(", ", unused.Select(f => f.Name.Trim())));

            foreach (var state in row.States)
                sb.AppendLine("- " + RenderState(state));

            // Dòng người dùng vừa thêm mà chưa điền gì: nói thẳng ra thay vì để một cái tên đứng trơ. Đây là
            // câu mở đường cho lượt kế của BA — họ thêm đối tượng vì biết nó cần có, phần "lưu gì" thì chính
            // là thứ họ đang chờ được hỏi.
            if (fields.Count == 0 && row.States.Count == 0)
                sb.AppendLine("- mình chưa rõ cần lưu những gì cho đối tượng này.");
        }

        // Đối tượng TỰ THÊM phải được gọi tên, cùng lý do với các dòng bị bỏ tích: đây là chỗ bảng khác đi so
        // với thứ BA vừa bày ra, và mọi tầng chắt lọc phía sau đọc bản kể này chứ không đọc cột DB. Nói rõ
        // nguồn gốc còn giữ cho BA khỏi hỏi lại "đối tượng này ở đâu ra" ở lượt kế.
        var addedByUser = rows.Where(r => r.Included && r.AddedByUser).ToList();
        if (addedByUser.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Các đối tượng mình tự bổ sung vào bảng: "
                + string.Join(", ", addedByUser.Select(r => r.Entity)) + ".");
        }

        var dropped = rows.Where(r => !r.Included).ToList();
        if (dropped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Các đối tượng mình KHÔNG cần: " + string.Join(", ", dropped.Select(r => r.Entity)) + ".");
        }

        return sb.ToString().TrimEnd();
    }

    // ==== chuẩn hoá từng phần ====

    private static string RenderField(EntityFieldNote field)
        => string.IsNullOrWhiteSpace(field.Meaning)
            ? field.Name.Trim()
            : $"{field.Name.Trim()} ({field.Meaning.Trim()})";

    private static string RenderState(EntityLifecycleState state)
    {
        var entry = string.IsNullOrWhiteSpace(state.EntryCondition) ? string.Empty : $" khi {state.EntryCondition.Trim()}";
        var notify = string.IsNullOrWhiteSpace(state.Notify) ? "không báo cho ai" : $"báo cho {state.Notify.Trim()}";
        return $"\"{state.State.Trim()}\"{entry} ⇒ {notify}";
    }

    private static List<EntityFieldNote> NormalizeFields(
        IEnumerable<EntityFieldNote>? proposed, IReadOnlySet<string> columnKeys, bool respectSelection)
    {
        var result = new List<EntityFieldNote>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in proposed ?? Enumerable.Empty<EntityFieldNote>())
        {
            if (field == null || string.IsNullOrWhiteSpace(field.Name))
                continue;

            var name = Clip(field.Name.Trim(), MaxTextChars);
            if (!seen.Add(Normalize(name)))
                continue;

            var meaning = Clip((field.Meaning ?? string.Empty).Trim(), MaxTextChars);
            // Thông tin trùng một CỘT ĐÃ TÍCH: người dùng đã chốt nó ở bảng cột, nên ô ý nghĩa được ghi rõ
            // nguồn thay vì bày ra như một đề xuất mới chờ duyệt lần hai.
            if (columnKeys.Contains(Normalize(name)) && meaning.Length == 0)
                meaning = "đã chốt ở bảng cột của tài liệu nguồn";

            result.Add(new EntityFieldNote
            {
                Name = name,
                Meaning = meaning,
                Used = !respectSelection || field.Used
            });

            if (result.Count >= MaxFieldsPerEntity)
                break;
        }

        return result;
    }

    /// <summary>
    /// Vòng đời của một đối tượng. <paramref name="respectSelection"/> = đường GỬI, và ở đó luật "một trạng
    /// thái không phải vòng đời" KHÔNG áp: nó dựng để chặn model bơm một vòng đời ra từ một danh từ, còn một
    /// dòng trạng thái người dùng vừa tự gõ (hoặc còn lại sau khi họ xóa bớt) là một quyết định. Cắt nó ở
    /// đường này gây đúng hai thiệt hại mà bảng sinh ra để chặn: mất chữ họ vừa gõ, và — với đối tượng danh
    /// mục vừa được thêm một trạng thái duy nhất — làm cả dòng rơi khỏi bảng theo luật "rỗng ruột".
    /// </summary>
    private static List<EntityLifecycleState> NormalizeStates(
        IEnumerable<EntityLifecycleState>? proposed, bool respectSelection = false)
    {
        var result = new List<EntityLifecycleState>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var state in proposed ?? Enumerable.Empty<EntityLifecycleState>())
        {
            if (state == null || string.IsNullOrWhiteSpace(state.State))
                continue;

            var name = Clip(state.State.Trim(), MaxTextChars);
            if (!seen.Add(Normalize(name)))
                continue;

            result.Add(new EntityLifecycleState
            {
                State = name,
                EntryCondition = Clip((state.EntryCondition ?? string.Empty).Trim(), MaxTextChars),
                Notify = Clip((state.Notify ?? string.Empty).Trim(), MaxTextChars)
            });

            if (result.Count >= MaxStatesPerEntity)
                break;
        }

        // Một trạng thái không phải vòng đời — xem ghi chú class. Đối tượng vẫn giữ (nó là danh mục).
        return respectSelection || result.Count >= MinStatesForLifecycle
            ? result
            : new List<EntityLifecycleState>();
    }

    private static string Normalize(string value)
        => string.Join(' ', (value ?? string.Empty).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim(' ', '.', ',', ':', ';', '-', '–');

    private static string Clip(string value, int max)
        => value.Length > max ? value[..max] : value;
}
