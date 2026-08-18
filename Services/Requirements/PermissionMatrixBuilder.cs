using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng phân quyền" — bảng mà người dùng chọn vai trò nào được làm gì trên từng màn
/// hình, và trong PHẠM VI DỮ LIỆU nào (xem <see cref="PermissionMatrixRow"/>).
///
/// <para>
/// Lớp này là CHỐT CHẶN TẤT ĐỊNH của tính năng, cùng vai trò với <see cref="SourceColumnMapBuilder"/> ở
/// bảng cột, và vá đúng ba đường hỏng đã gặp:
/// </para>
/// <list type="bullet">
///   <item><b>Màn hình bịa.</b> Mọi dòng phải khớp một mục trong <c>Project.PlannedScope</c> — danh sách
///   màn hình/tính năng đã chắt từ chính hội thoại. Không có chốt này thì model thêm được một màn hình
///   chưa ai nói tới, người dùng tích quyền cho nó, và một tính năng ngoài phạm vi đi thẳng vào tài liệu
///   mang theo chữ ký của người dùng.</item>
///   <item><b>Màn hình bị bỏ quên.</b> Ngược lại và nguy hiểm hơn: model chỉ nêu vài màn hình nó nhớ, các
///   màn hình còn lại biến mất khỏi bảng và mặc nhiên "không ai có quyền" mà người dùng không bao giờ nhìn
///   thấy để phản đối — đúng khiếm khuyết của hàng chip mà bảng sinh ra để chữa. Vì vậy <see cref="Build"/>
///   luôn BỔ SUNG mọi mục phạm vi còn thiếu vào cuối bảng.</item>
///   <item><b>Cột lỗ chỗ.</b> Một vai trò chỉ xuất hiện ở vài dòng thì các dòng còn lại không có ô nào cho
///   vai đó — không phải "không có quyền", mà là "không hỏi". Bản chuẩn hoá bơm ĐỦ mọi vai vào MỌI dòng.</item>
/// </list>
///
/// <para>
/// <b>Bộ vai trò (= CỘT) là một BẢNG người dùng tự sửa được, không phải thứ suy ra từ đề xuất của model.</b>
/// Bảng "Vai trò" đứng ngay trên các bảng màn hình cho thêm / sửa chữ / xóa từng vai, và bộ đó là nguồn của
/// MỌI cột bên dưới. Trước đó cột được chắt ngầm từ chính grants model trả về (<see cref="CollectRoles"/>),
/// nên người dùng không có đường nào thêm một vai có thật mà model quên — lối thoát duy nhất là gõ vào khung
/// chat để BA bày lại bảng, tức một lượt LLM cho một việc tất định. Vì vậy <see cref="Build"/> nhận danh
/// sách vai TƯỜNG MINH: có thì nó thắng, không thì rơi về bản chắt cũ (lượt BA bày bảng, và các tab mở từ
/// trước bản này).
/// </para>
///
/// <para>
/// <b>Luật bằng chứng</b> (<see cref="PermissionGrant.Locked"/>): server chỉ khóa một ô khi model kèm được
/// TRÍCH DẪN cho nó. Không có trích dẫn thì ô vẫn hiện đề xuất của BA nhưng ở dạng sửa được và được đánh
/// dấu là phỏng đoán. Đây là ranh giới giữ cho bảng khỏi thành một cái chip "Đồng ý phương án này" phóng
/// to: nếu mọi ô đều được điền sẵn và trông như đã chốt, người dùng bấm gửi trong ba giây và ta quay về
/// đúng lỗi mà bảng sinh ra để chữa — một thao tác trở thành bằng chứng cho hàng chục quyền.
/// </para>
/// </summary>
public static class PermissionMatrixBuilder
{
    /// <summary>Trần số dòng của bảng. Cao hơn trần cột (40) vì một màn hình có nhiều chức năng.</summary>
    public const int MaxRows = 80;

    /// <summary>Trần số vai trò = số CỘT. Quá số này thì bảng không đọc nổi trên màn hình thường.</summary>
    public const int MaxRoles = 8;

    /// <summary>Trần số chức năng của MỘT màn hình — giữ bảng khỏi phình vì model liệt kê quá tay.</summary>
    public const int MaxFunctionsPerScreen = 8;

    private const int MaxTextChars = 200;
    private const int MaxEvidenceChars = 300;

    /// <summary>Chức năng mặc định cho màn hình mà model không đề xuất dòng nào — luôn có ít nhất "ai xem được".</summary>
    private const string DefaultFunction = "Xem";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Bảng cuối cùng: giữ các dòng khớp phạm vi đã chắt (lấy lại đúng chữ của <paramref name="plannedScope"/>),
    /// bỏ dòng bịa/trùng, bổ sung mọi màn hình chưa được nhắc tới, rồi bơm đủ vai trò vào mọi dòng. Thứ tự
    /// luôn theo thứ tự của <paramref name="plannedScope"/> — người dùng đang đối chiếu với phạm vi họ vừa
    /// chốt ở bảng màn hình. Trả rỗng khi phạm vi trống hoặc không đề xuất nào dùng được.
    ///
    /// <para>
    /// <paramref name="roles"/> là bộ CỘT do người dùng rà trên bảng "Vai trò". Có mục nào thì nó quyết định
    /// trọn vẹn: vai không nằm trong đó bị bỏ khỏi mọi dòng (người dùng vừa xóa nó), còn vai nằm trong đó mà
    /// chưa dòng nào nhắc tới vẫn thành một cột rỗng để họ điền. Rỗng/null ⇒ chắt từ chính grants như trước —
    /// đó là đường của lượt BA BÀY bảng, khi bảng vai trò chưa tồn tại.
    /// </para>
    /// </summary>
    public static List<PermissionMatrixRow> Build(
        IEnumerable<PermissionMatrixRow>? proposed,
        IReadOnlyList<string> plannedScope,
        IReadOnlyList<string>? roles = null)
    {
        var result = new List<PermissionMatrixRow>();
        var screens = CleanScreens(plannedScope);
        if (screens.Count == 0)
            return result;

        var rows = (proposed ?? Enumerable.Empty<PermissionMatrixRow>())
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Function))
            .ToList();
        if (rows.Count == 0)
            return result;

        // Vai trò của CẢ bảng: bộ người dùng đã rà nếu có, không thì thứ tự model nêu lần đầu. Một bảng
        // không có vai nào là bảng vô nghĩa — trả rỗng để caller fail-open (lượt chạy như hội thoại thường,
        // hoặc đường GỬI từ chối lưu) thay vì dựng một bảng không có cột.
        var columns = SanitizeRoles(roles);
        if (columns.Count == 0)
            columns = CollectRoles(rows);
        if (columns.Count == 0)
            return result;

        // Gom đề xuất về đúng màn hình THẬT. Dòng không khớp mục phạm vi nào bị bỏ ở đây.
        var byScreen = new Dictionary<string, List<PermissionMatrixRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var screen = MatchScreen(row.Screen, screens);
            if (screen == null)
                continue;

            if (!byScreen.TryGetValue(screen, out var bucket))
                byScreen[screen] = bucket = new List<PermissionMatrixRow>();

            var function = Clip(row.Function.Trim(), MaxTextChars);
            if (bucket.Count >= MaxFunctionsPerScreen
                || bucket.Any(r => string.Equals(r.Function, function, StringComparison.OrdinalIgnoreCase)))
                continue;

            bucket.Add(new PermissionMatrixRow
            {
                Screen = screen, // chữ của PHẠM VI ĐÃ CHẮT, không phải chữ của model
                Function = function,
                Condition = Clip((row.Condition ?? string.Empty).Trim(), MaxTextChars),
                Grants = NormalizeGrants(row.Grants, columns)
            });
        }

        foreach (var screen in screens)
        {
            // Màn hình model bỏ quên: vẫn phải có mặt, ở trạng thái chưa ai có quyền. Im lặng bỏ nó đi là
            // biến "chưa hỏi" thành "không ai được xem" mà không ai nhìn thấy để phản đối.
            var bucket = byScreen.TryGetValue(screen, out var found) && found.Count > 0
                ? found
                : new List<PermissionMatrixRow>
                {
                    new()
                    {
                        Screen = screen,
                        Function = DefaultFunction,
                        Grants = NormalizeGrants(null, columns)
                    }
                };

            foreach (var row in bucket)
            {
                result.Add(row);
                if (result.Count >= MaxRows)
                    return result;
            }
        }

        return result;
    }

    /// <summary>
    /// Bản chuẩn hoá cho dữ liệu ĐẾN TỪ TRÌNH DUYỆT (người dùng vừa chọn xong rồi gửi lên). Cùng luật với
    /// <see cref="Build"/> — server không tin bảng client gửi, kể cả khi chính server vừa render nó ra vài
    /// giây trước: tên màn hình vẫn phải khớp phạm vi đã chắt, và mọi dòng vẫn phải đủ vai trò.
    ///
    /// <para>
    /// Khác đúng MỘT điểm: ô do người dùng chọn KHÔNG mang bằng chứng nào, và cũng không cần —
    /// <see cref="PermissionGrant.Locked"/> chỉ dùng để phân biệt "BA đoán" với "hội thoại đã nói" TRONG
    /// lúc bảng còn chờ. Bảng đã gửi thì mọi ô đều là quyết định của người dùng, nên cờ đó được xoá sạch.
    /// </para>
    /// </summary>
    public static List<PermissionMatrixRow> Sanitize(
        IEnumerable<PermissionMatrixRow>? submitted,
        IReadOnlyList<string> plannedScope,
        IReadOnlyList<string>? roles = null)
    {
        var rows = Build(submitted, plannedScope, roles);
        foreach (var grant in rows.SelectMany(r => r.Grants))
        {
            grant.Locked = false;
            grant.Evidence = string.Empty;
        }
        return rows;
    }

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
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Screen) && !string.IsNullOrWhiteSpace(r.Function))
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
    /// Khối ngữ cảnh gắn vào MỌI lượt chat sau khi bảng đã chốt, vào lượt distill bản đồ bao phủ, và vào
    /// prompt sinh AI Design Spec. Không có khối này thì bảng chỉ là một màn bấm đẹp: BA vẫn hỏi lại đúng
    /// các quyền người dùng vừa chọn từng ô, bản đồ bao phủ vẫn không thấy bằng chứng nào cho nhóm phân
    /// quyền (nên cổng "Write Requirement" vẫn đóng và hỏi lại cùng một câu mãi), còn POC thì vẫn dựng màn
    /// hình không phân biệt vai. Trả null khi chưa chốt.
    /// </summary>
    public static string? RenderConfirmedBlock(string? json)
    {
        var rows = Parse(json);
        if (rows.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng phân quyền đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại các quyền này) ---");
        sb.AppendLine("Mỗi dòng: màn hình · chức năng · vai trò được làm, kèm PHẠM VI DỮ LIỆU trong ngoặc "
            + "(\"của mình\" = chỉ bản ghi do chính họ tạo; \"của đơn vị\" = bản ghi của nhân sự/đơn vị họ phụ "
            + "trách; \"tất cả\" = toàn bộ). Vai trò KHÔNG được nêu ở một dòng là vai KHÔNG có quyền đó.");

        foreach (var group in rows.GroupBy(r => r.Screen, StringComparer.Ordinal))
        {
            sb.AppendLine($"* {group.Key}");
            foreach (var row in group)
            {
                var granted = row.Grants
                    .Where(g => PermissionScope.IsGranted(g.Scope) && !string.IsNullOrWhiteSpace(g.Role))
                    .Select(g => $"{g.Role.Trim()} ({g.Scope.Trim()})")
                    .ToList();

                var who = granted.Count > 0 ? string.Join(", ", granted) : "KHÔNG vai nào";
                var condition = string.IsNullOrWhiteSpace(row.Condition) ? string.Empty : $" — điều kiện: {row.Condition.Trim()}";
                sb.AppendLine($"  - {row.Function}: {who}{condition}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Tin nhắn mà TRÌNH DUYỆT soạn từ bảng đã chốt rồi gửi qua đúng đường chat thường — bảng đi vào hội
    /// thoại bằng cửa của người dùng, y như bảng cột. Để ở server (thay vì viết lại trong JS) vì đây cũng
    /// là bản mà test đọc: một thay đổi câu chữ ở đây không được phép âm thầm làm lệch thứ đi vào transcript.
    /// </summary>
    public static string RenderUserMessage(IReadOnlyList<PermissionMatrixRow> rows)
    {
        if (rows.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Mình đã rà bảng phân quyền, đây là quyền của từng vai trò trên từng màn hình:");

        foreach (var group in rows.GroupBy(r => r.Screen, StringComparer.Ordinal))
        {
            sb.AppendLine();
            sb.AppendLine($"{group.Key}:");
            foreach (var row in group)
            {
                var granted = row.Grants
                    .Where(g => PermissionScope.IsGranted(g.Scope) && !string.IsNullOrWhiteSpace(g.Role))
                    .Select(g => $"{g.Role.Trim()} ({g.Scope.Trim()})")
                    .ToList();

                // Dòng KHÔNG ai được làm cũng phải nói ra: im lặng bỏ nó thì người dùng không có bằng chứng
                // nào cho thấy mình vừa loại đúng thứ định loại — cùng lý do bảng cột gọi tên cả cột bị bỏ.
                var who = granted.Count > 0 ? string.Join(", ", granted) : "không vai nào được làm";
                var condition = string.IsNullOrWhiteSpace(row.Condition) ? string.Empty : $" [điều kiện: {row.Condition.Trim()}]";
                sb.AppendLine($"- {row.Function}: {who}{condition}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ==== chuẩn hoá từng phần ====

    private static List<string> CleanScreens(IReadOnlyList<string>? plannedScope)
    {
        var result = new List<string>();
        if (plannedScope == null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in plannedScope)
        {
            var screen = (raw ?? string.Empty).Trim();
            if (screen.Length == 0 || !seen.Add(Normalize(screen)))
                continue;

            result.Add(Clip(screen, MaxTextChars));
            if (result.Count >= MaxRows)
                break;
        }
        return result;
    }

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

    // Model trả tự do ("own", "chỉ của mình", "toàn bộ", "x", "có"…) — kéo về đúng bốn nấc. Không nhận diện
    // được mà vẫn là một chuỗi có nghĩa thì hiểu là CÓ quyền ở mức rộng nhất người dùng còn sửa được;
    // rỗng/"không" ⇒ không có quyền.
    private static string NormalizeScope(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
            return PermissionScope.None;

        if (NoCues.Any(c => value == c))
            return PermissionScope.None;
        if (OwnCues.Any(c => value.Contains(c, StringComparison.Ordinal)))
            return PermissionScope.Own;
        if (UnitCues.Any(c => value.Contains(c, StringComparison.Ordinal)))
            return PermissionScope.Unit;
        return PermissionScope.All;
    }

    private static readonly string[] NoCues = { "không", "khong", "none", "no", "-", "0", "false" };
    private static readonly string[] OwnCues = { "của mình", "cua minh", "own", "self", "mình lập", "do mình" };
    private static readonly string[] UnitCues = { "đơn vị", "don vi", "unit", "team", "thuộc quyền", "phòng ban", "cấp dưới" };

    // Ghép tên màn hình model đưa về đúng mục phạm vi đã chắt. Khớp CHÍNH XÁC trước; không có thì cho phép
    // một bên chứa bên kia — mục phạm vi là câu dài ("Màn hình Training Plan để tạo và quản lý kế hoạch cho
    // cả năm, gồm Name, Version và Created Date") còn model hay rút gọn ("Màn hình Training Plan"). Ngưỡng
    // độ dài giữ cho những mẩu quá ngắn ("Xem", "Plan") khỏi dính vào mọi mục.
    private const int MinContainsLength = 8;

    private static string? MatchScreen(string? proposed, IReadOnlyList<string> screens)
    {
        var value = Normalize(proposed ?? string.Empty);
        if (value.Length == 0)
            return null;

        foreach (var screen in screens)
        {
            if (Normalize(screen) == value)
                return screen;
        }

        if (value.Length < MinContainsLength)
            return null;

        // Nhiều mục cùng chứa mẩu đó ⇒ mơ hồ, bỏ hẳn. Gán bừa vào mục đầu tiên là đặt quyền lên nhầm màn
        // hình, thứ không ai phát hiện được khi đọc lại bảng.
        var matches = screens.Where(s =>
        {
            var normalized = Normalize(s);
            return normalized.Contains(value, StringComparison.Ordinal)
                || (normalized.Length >= MinContainsLength && value.Contains(normalized, StringComparison.Ordinal));
        }).ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Normalize(string value)
        => string.Join(' ', value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim(' ', '.', ',', ':', ';', '-', '–');

    private static string Clip(string value, int max)
        => value.Length > max ? value[..max] : value;
}
