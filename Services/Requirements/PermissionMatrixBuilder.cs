using ICOGenerator.Localization;
using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using static ICOGenerator.Services.Requirements.RequirementText;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng phân quyền" — vai trò nào được làm TÁC VỤ nào của bản thiết kế UX, và trong PHẠM VI
/// DỮ LIỆU nào (xem <see cref="PermissionMatrixRow"/>). BA soạn bảng từ hội thoại
/// (<see cref="PermissionMatrixDraftService"/>); người dùng xem/sửa ở popup của panel tiến độ.
///
/// <para>
/// Lớp này là CHỐT CHẶN TẤT ĐỊNH của tính năng, cùng vai trò với <see cref="SourceColumnMapBuilder"/> ở
/// bảng cột, và vá đúng ba đường hỏng đã gặp:
/// </para>
/// <list type="bullet">
///   <item><b>Dòng bịa.</b> Dòng của bảng LÀ các tác vụ của bản thiết kế, theo đúng thứ tự màn hình. Model chỉ
///   điền ai được làm và phạm vi dữ liệu; dòng trỏ vào tác vụ không có thật bị bỏ, nên không có đường cho
///   một tính năng ngoài phạm vi đi vào tài liệu.</item>
///   <item><b>Dòng bị bỏ quên.</b> Ngược lại và nguy hiểm hơn: model chỉ nêu vài tác vụ nó nhớ, các tác vụ còn
///   lại mặc nhiên "không ai có quyền". Vì vậy <see cref="Build"/> luôn có ĐỦ mọi tác vụ, tác vụ model bỏ quên
///   ở trạng thái chưa ai có quyền — người dùng thấy nó trong popup và điền được.</item>
///   <item><b>Cột lỗ chỗ.</b> Một vai trò chỉ xuất hiện ở vài dòng thì các dòng còn lại không có ô nào cho
///   vai đó — không phải "không có quyền", mà là "không hỏi". Bản chuẩn hoá bơm ĐỦ mọi vai vào MỌI dòng.</item>
/// </list>
///
/// <para>
/// <b>Bộ vai trò (= CỘT) là một BẢNG người dùng tự sửa được, không phải thứ suy ra từ đề xuất của model.</b>
/// Bảng "Vai trò" đứng ngay trên các bảng màn hình trong popup cho thêm / sửa chữ / xóa từng vai, và bộ đó
/// là nguồn của MỌI cột bên dưới. Vì vậy <see cref="Build"/> nhận danh sách vai TƯỜNG MINH: có thì nó thắng,
/// không thì rơi về bản chắt từ chính grants model trả về (<see cref="CollectRoles"/>) — đường của lượt BA
/// soạn bảng, khi bảng vai trò chưa tồn tại.
/// </para>
///
/// <para>
/// <b>Luật bằng chứng</b> (<see cref="PermissionGrant.Locked"/>): server chỉ đánh dấu một ô là "người dùng đã
/// nói" khi model kèm được TRÍCH DẪN cho nó. Ô có trích dẫn hiện ✓ kèm câu gốc trong popup; ô không có là
/// suy luận của BA từ luồng. Người dùng sửa được mọi ô — lưu xong thì mọi ô đều là quyết định của họ
/// (<see cref="Sanitize"/> xoá cờ).
/// </para>
/// </summary>
public static class PermissionMatrixBuilder
{
    /// <summary>Trần số dòng của bảng — luôn ≥ trần tác vụ của bản thiết kế (<see cref="UxInventoryBuilder.MaxTasks"/>).</summary>
    public const int MaxRows = 80;

    /// <summary>Trần số vai trò = số CỘT. Quá số này thì bảng không đọc nổi trên màn hình thường.</summary>
    public const int MaxRoles = 8;

    private const int MaxTextChars = 200;
    private const int MaxEvidenceChars = 300;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Bảng cuối cùng: MỘT dòng cho MỖI tác vụ của <paramref name="blueprint"/> (theo thứ tự màn hình), lấy quyền
    /// từ đề xuất khớp ID tác vụ (hoặc khớp tên tác vụ khi model quên ID), rồi bơm đủ vai trò vào mọi dòng.
    /// Tác vụ model bỏ quên vẫn có dòng, ở trạng thái chưa ai có quyền. Trả rỗng khi bản thiết kế chưa có tác
    /// vụ nào hoặc không có vai nào.
    ///
    /// <para>
    /// <paramref name="keep"/> (tuỳ chọn) là bảng ĐANG LƯU: dòng của tác vụ đã có trong đó thắng mọi đề xuất —
    /// đó là đường BỔ SUNG dòng cho tác vụ mới mà không đụng một ô nào người dùng đã sửa ở popup.
    /// </para>
    ///
    /// <para>
    /// <paramref name="roles"/> là bộ CỘT do người dùng rà trên bảng "Vai trò" của popup. Có mục nào thì nó
    /// quyết định trọn vẹn: vai không nằm trong đó bị bỏ khỏi mọi dòng (người dùng vừa xóa nó), còn vai nằm
    /// trong đó mà chưa dòng nào nhắc tới vẫn thành một cột rỗng để họ điền. Rỗng/null ⇒ lấy cột của bảng đang
    /// lưu, rồi tới cột chắt từ chính grants — đó là đường của lượt BA SOẠN bảng.
    /// </para>
    /// </summary>
    public static List<PermissionMatrixRow> Build(
        IEnumerable<PermissionMatrixRow>? proposed,
        UxBlueprint blueprint,
        IReadOnlyList<string>? roles = null,
        IEnumerable<PermissionMatrixRow>? keep = null)
    {
        var result = new List<PermissionMatrixRow>();
        var tasks = OrderedTasks(blueprint);
        if (tasks.Count == 0)
            return result;

        var kept = (keep ?? Enumerable.Empty<PermissionMatrixRow>()).Where(r => r != null && r.TaskId.Length > 0).ToList();
        var rows = (proposed ?? Enumerable.Empty<PermissionMatrixRow>()).Where(r => r != null).ToList();

        var columns = SanitizeRoles(roles);
        if (columns.Count == 0)
            columns = CollectRoles(kept);
        if (columns.Count == 0)
            columns = CollectRoles(rows);
        if (columns.Count == 0)
            return result;

        foreach (var task in tasks)
        {
            var source = kept.FirstOrDefault(r => SameTask(r, task))
                         ?? rows.FirstOrDefault(r => SameTask(r, task))
                         ?? rows.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.TaskId) && Normalize(r.Function) == Normalize(task.Name));

            result.Add(new PermissionMatrixRow
            {
                TaskId = task.Id,
                Screen = ScreenOf(blueprint, task),
                Function = Clip(task.Name, MaxTextChars),
                Condition = Clip((source?.Condition ?? string.Empty).Trim(), MaxTextChars),
                Grants = NormalizeGrants(source?.Grants, columns)
            });
            if (result.Count >= MaxRows)
                break;
        }

        return result;
    }

    /// <summary>
    /// Bản chuẩn hoá cho dữ liệu ĐẾN TỪ TRÌNH DUYỆT (người dùng vừa sửa ở popup rồi lưu). Cùng luật với
    /// <see cref="Build"/> — server không tin bảng client gửi, kể cả khi chính server vừa render nó ra vài
    /// giây trước: dòng vẫn phải là tác vụ có thật, và mọi dòng vẫn phải đủ vai trò.
    ///
    /// <para>
    /// Khác đúng MỘT điểm: ô do người dùng chọn KHÔNG mang bằng chứng nào, và cũng không cần —
    /// <see cref="PermissionGrant.Locked"/> chỉ dùng để phân biệt "BA suy ra" với "hội thoại đã nói" ở bảng
    /// BA soạn. Bảng người dùng đã lưu thì mọi ô đều là quyết định của họ, nên cờ đó được xoá sạch.
    /// </para>
    /// </summary>
    public static List<PermissionMatrixRow> Sanitize(
        IEnumerable<PermissionMatrixRow>? submitted,
        UxBlueprint blueprint,
        IReadOnlyList<string>? roles = null)
    {
        var rows = Build(submitted, blueprint, roles);
        foreach (var grant in rows.SelectMany(r => r.Grants))
        {
            grant.Locked = false;
            grant.Evidence = string.Empty;
        }
        return rows;
    }

    /// <summary>
    /// Làm tươi bảng ĐANG LƯU theo bản thiết kế vừa ghi: cột hiển thị (màn hình, tên tác vụ) theo bản mới, thứ
    /// tự theo thứ tự màn hình, dòng của tác vụ đã bỏ đi theo tác vụ đó. KHÔNG thêm dòng — dòng cho tác vụ mới
    /// do lượt soạn phân quyền bổ sung (<see cref="PermissionMatrixGate.MissingTasks"/>), có đề xuất quyền thật
    /// thay vì một dòng trắng. Trả null khi chưa có bảng hoặc không có gì đổi.
    /// </summary>
    public static string? Rebind(string? json, UxBlueprint blueprint)
    {
        var rows = Parse(json);
        if (rows.Count == 0)
            return null;

        var rebound = new List<PermissionMatrixRow>();
        foreach (var task in OrderedTasks(blueprint))
        {
            var row = rows.FirstOrDefault(r => SameTask(r, task));
            if (row == null)
                continue;
            rebound.Add(new PermissionMatrixRow
            {
                TaskId = task.Id,
                Screen = ScreenOf(blueprint, task),
                Function = Clip(task.Name, MaxTextChars),
                Condition = row.Condition,
                Grants = row.Grants
            });
        }

        var serialized = JsonSerializer.Serialize(rebound);
        return serialized == JsonSerializer.Serialize(rows) ? null : serialized;
    }

    /// <summary>Tác vụ theo thứ tự hiển thị: theo màn hình đặt chúng (thứ tự thiết kế), tác vụ chưa có màn ở cuối.</summary>
    public static List<UxTask> OrderedTasks(UxBlueprint blueprint)
    {
        var ordered = new List<UxTask>();
        foreach (var action in blueprint.Screens.SelectMany(s => s.Actions))
        {
            var task = blueprint.Tasks.FirstOrDefault(t => UxBlueprintBuilder.SameId(t.Id, action.Task));
            if (task != null && !ordered.Contains(task))
                ordered.Add(task);
        }
        ordered.AddRange(blueprint.Tasks.Where(t => !ordered.Contains(t)));
        return ordered;
    }

    private static bool SameTask(PermissionMatrixRow row, UxTask task)
        => row.TaskId.Length > 0 && UxBlueprintBuilder.SameId(row.TaskId, task.Id);

    private static string ScreenOf(UxBlueprint blueprint, UxTask task)
        => UxBlueprintBuilder.HomeOf(blueprint, task.Id)?.Name ?? task.Object;

    /// <summary>Đọc JSON bảng phân quyền đã lưu (cột DB hoặc payload client). null/rỗng/hỏng ⇒ mảng rỗng.</summary>
    public static List<PermissionMatrixRow> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<PermissionMatrixRow>();

        try
        {
            var rows = JsonSerializer.Deserialize<List<PermissionMatrixRow>>(json, ReadOptions)
                ?? new List<PermissionMatrixRow>();
            return rows
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Function))
                .Select(r =>
                {
                    r.TaskId ??= string.Empty;
                    r.Screen ??= string.Empty;
                    r.Grants ??= new List<PermissionGrant>();
                    return r;
                })
                .ToList();
        }
        catch
        {
            return new List<PermissionMatrixRow>();
        }
    }

    /// <summary>
    /// Chuẩn hoá một danh sách vai trò ĐẾN TỪ TRÌNH DUYỆT (bảng "Vai trò" người dùng vừa rà): bỏ mục rỗng,
    /// cắt theo trần độ dài, bỏ trùng theo cùng phép so khớp mà cả builder dùng, chặn ở
    /// <see cref="MaxRoles"/>. Giữ nguyên THỨ TỰ người dùng sắp — đó là thứ tự cột họ đang nhìn.
    ///
    /// <para>
    /// Bỏ trùng theo <see cref="Normalize"/> chứ không theo chuỗi thô là bắt buộc: "HRBP" và "hrbp " là hai
    /// dòng khác nhau trên bảng nhưng cùng một cột lúc so khớp, nên để cả hai lọt vào là dựng ra hai cột
    /// không phân biệt được mà quyền thì rơi vào cùng một vai.
    /// </para>
    /// </summary>
    public static List<string> SanitizeRoles(IEnumerable<string>? roles)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in roles ?? Enumerable.Empty<string>())
        {
            var value = Clip((raw ?? string.Empty).Trim(), MaxTextChars);
            if (value.Length == 0 || !seen.Add(Normalize(value)))
                continue;

            result.Add(value);
            if (result.Count >= MaxRoles)
                break;
        }

        return result;
    }

    /// <summary>Đọc JSON danh sách vai trò do trình duyệt gửi kèm bảng. null/rỗng/hỏng ⇒ mảng rỗng.</summary>
    public static List<string> ParseRoles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, ReadOptions) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>Các vai trò (cột) của một bảng đã lưu, theo thứ tự xuất hiện. Rỗng khi chưa chốt.</summary>
    public static List<string> Roles(string? json)
    {
        var roles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grant in Parse(json).SelectMany(r => r.Grants))
        {
            var role = (grant.Role ?? string.Empty).Trim();
            if (role.Length > 0 && seen.Add(role))
                roles.Add(role);
        }
        return roles;
    }

    /// <summary>
    /// Khối ngữ cảnh gắn vào MỌI lượt chat sau khi bảng đã có, vào lượt distill bản đồ bao phủ, và vào
    /// prompt sinh AI Design Spec. Không có khối này thì BA hỏi lại các quyền đã có, bản đồ bao phủ không
    /// thấy bằng chứng nào cho nhóm phân quyền (nên cổng "Write Requirement" vẫn đóng), còn POC thì dựng màn
    /// hình không phân biệt vai. Trả null khi chưa có bảng.
    /// </summary>
    public static string? RenderConfirmedBlock(string? json)
    {
        var rows = Parse(json);
        if (rows.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng phân quyền của ứng dụng (BA soạn từ hội thoại, người dùng sửa được — đừng hỏi lại các quyền này) ---");
        sb.AppendLine("Mỗi dòng: màn hình · chức năng · vai trò được làm, kèm PHẠM VI DỮ LIỆU trong ngoặc "
            + $"(\"{PermissionScope.Label(PermissionScope.Own)}\" = chỉ bản ghi do chính họ tạo; "
            + $"\"{PermissionScope.Label(PermissionScope.Unit)}\" = bản ghi của nhân sự/đơn vị họ phụ trách; "
            + $"\"{PermissionScope.Label(PermissionScope.All)}\" = toàn bộ). Vai trò KHÔNG được nêu ở một dòng là vai KHÔNG có quyền đó.");

        foreach (var group in rows.GroupBy(r => r.Screen, StringComparer.Ordinal))
        {
            sb.AppendLine($"* {group.Key}");
            foreach (var row in group)
            {
                var granted = row.Grants
                    .Where(g => PermissionScope.IsGranted(g.Scope) && !string.IsNullOrWhiteSpace(g.Role))
                    .Select(g => $"{g.Role.Trim()} ({PermissionScope.Label(g.Scope)})")
                    .ToList();

                var who = granted.Count > 0 ? string.Join(", ", granted) : "KHÔNG vai nào";
                var condition = string.IsNullOrWhiteSpace(row.Condition) ? string.Empty : $" — điều kiện: {row.Condition.Trim()}";
                sb.AppendLine($"  - {row.Function}: {who}{condition}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ==== chuẩn hoá từng phần ====

    private static List<string> CollectRoles(IEnumerable<PermissionMatrixRow> rows)
    {
        var roles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grant in rows.SelectMany(r => r.Grants ?? new List<PermissionGrant>()))
        {
            var role = (grant?.Role ?? string.Empty).Trim();
            if (role.Length == 0 || role.Length > MaxTextChars || !seen.Add(role))
                continue;

            roles.Add(role);
            if (roles.Count >= MaxRoles)
                break;
        }
        return roles;
    }

    // Bơm ĐỦ vai trò của bảng vào một dòng, theo đúng thứ tự cột. Vai không được model nêu trên dòng này ⇒
    // ô trống (không có quyền) — nhưng ô đó VẪN CÓ MẶT để người dùng nhìn thấy và chọn.
    private static List<PermissionGrant> NormalizeGrants(IEnumerable<PermissionGrant>? proposed, IReadOnlyList<string> roles)
    {
        var byRole = new Dictionary<string, PermissionGrant>(StringComparer.OrdinalIgnoreCase);
        foreach (var grant in proposed ?? Enumerable.Empty<PermissionGrant>())
        {
            var role = (grant?.Role ?? string.Empty).Trim();
            if (role.Length == 0 || byRole.ContainsKey(role))
                continue;
            byRole[role] = grant!;
        }

        return roles.Select(role =>
        {
            byRole.TryGetValue(role, out var grant);
            var evidence = Clip((grant?.Evidence ?? string.Empty).Trim(), MaxEvidenceChars);
            return new PermissionGrant
            {
                Role = role,
                Scope = NormalizeScope(grant?.Scope),
                // LUẬT BẰNG CHỨNG: khóa một ô là tuyên bố "người dùng đã tự nói điều này". Server không
                // nhận tuyên bố đó từ một lá cờ — phải có trích dẫn kèm theo. Cờ suông ⇒ ô vẫn hiện đề
                // xuất nhưng ở dạng sửa được, tức người dùng vẫn phải tự quyết.
                Locked = evidence.Length > 0,
                Evidence = evidence
            };
        }).ToList();
    }

    /// <summary>
    /// Kéo giá trị phạm vi về đúng bốn nấc. Prompt và trình duyệt đều gửi đúng MÃ
    /// (<see cref="PermissionScope"/>), nên đường chính là so nguyên văn. Phần còn lại là lưới đỡ cho MODEL,
    /// thứ vẫn viết tự do ("chỉ của mình", "toàn bộ", "own records") dù
    /// <c>permission-matrix-draft.v1.md</c> đã kê sẵn ba mã: so với nhãn và các cụm gợi ý của từng nấc ở mọi
    /// ngôn ngữ (resource <c>PermissionScope.Cues.*</c>).
    ///
    /// <para>
    /// <b>Không nhận diện được ⇒ Ô TRỐNG, không phải "tất cả".</b> Bản trước rơi về nấc RỘNG NHẤT, tức
    /// một câu model viết lệch ("chỉ bản ghi liên quan") thành quyền toàn hệ thống trong bảng người dùng
    /// sắp bấm gửi. Nó cũng đi ngược đúng luật của chính bảng này: ô BA suy đoán thì để TRỐNG và người
    /// dùng tự chọn (xem <see cref="PermissionGrant.Locked"/>) — một chuỗi không đọc được là một phán
    /// đoán không đọc được, không phải một quyết định. Ô trống hiện thành ô chọn còn nguyên, nên cái giá
    /// của hướng an toàn này là người dùng tự tích lại một ô; hướng cũ thì cái giá là một quyền rộng
    /// hơn thực tế đi thẳng vào tài liệu mà không ai gõ ra nó.
    /// </para>
    /// </summary>
    private static string NormalizeScope(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
            return PermissionScope.None;

        if (PermissionScope.Granted.Contains(value))
            return value;

        // Thứ tự là một phần của luật: "không" phải khớp NGUYÊN chuỗi (nó nằm trong nhiều câu khác), và
        // nấc hẹp được xét trước nấc rộng — "tất cả của mình" là "của mình".
        if (Cues("none").Any(c => value == c))
            return PermissionScope.None;
        foreach (var scope in PermissionScope.Granted)
        {
            if (Cues(scope).Any(c => value.Contains(c, StringComparison.Ordinal)))
                return scope;
        }

        return PermissionScope.None;
    }

    private static IEnumerable<string> Cues(string scope)
    {
        var cues = Loc.InAllLanguages("PermissionScope.Cues." + scope).SelectMany(LocList.Split);
        return scope == "none"
            ? cues
            : cues.Concat(Loc.InAllLanguages("PermissionScope.Label." + scope).Select(x => x.ToLowerInvariant()));
    }
}
