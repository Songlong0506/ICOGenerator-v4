using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng đối tượng nghiệp vụ" — các thứ có hồ sơ riêng trong ứng dụng, thông tin cần lưu
/// về chúng, và vòng đời trạng thái chúng đi qua (xem <see cref="EntityMapRow"/>). Người nhận thông báo ở
/// mỗi chuyển trạng thái được chốt ở bảng RIÊNG đứng sau — xem <see cref="NotificationMapBuilder"/>.
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
///   <item><b>Ô ngoài nhánh của hai trục.</b> Mỗi thông tin có hai trục độc lập — <c>Input</c> (nhập thế
///   nào) và <c>Source</c> (danh sách lấy ở đâu, chỉ có nghĩa với hai kiểu chọn). Ô của nhánh KHÔNG được
///   chọn bị cắt hẳn chứ không chỉ ẩn đi ở giao diện: người dùng đổi "chọn nhiều" sang "gõ tay" thì các giá
///   trị họ gõ lúc trước vẫn nằm trong payload, và một danh sách treo dưới một ô gõ tay sẽ được cả spec lẫn
///   POC đọc như thật. Cùng luật ép <c>Required</c> về false ở hai ca nó vô nghĩa (thông tin không lưu, và
///   ô ứng dụng tự sinh) — xem <see cref="NormalizeFields"/>.</item>
/// </list>
///
/// <para>
/// Một giá trị của trục thứ hai đi XA hơn mô hình dữ liệu: <c>app</c> ("ứng dụng tự quản lý danh mục này")
/// nghĩa là dự án cần thêm một MÀN HÌNH. Xem <see cref="ManagedListScreens(IEnumerable{EntityMapRow})"/>.
/// </para>
///
/// <para>
/// Hai chốt chặn đầu nhắm vào MODEL, nên cả hai đều NHƯỜNG ở đường GỬI: người dùng thêm/xóa được dòng ngay
/// trên bảng (nút "+ thêm đối tượng", "+ thêm thông tin", "+ thêm trạng thái"), và một dòng họ vừa tự gõ mà
/// biến mất lúc lưu là đúng loại quyết định câm mà cả bảng này sinh ra để chặn. Xem
/// <see cref="EntityMapRow.AddedByUser"/> và <see cref="Sanitize"/>.
/// </para>
///
/// <para>
/// <b>Ô MÔ TẢ không phải một quyết định, nên nó KHÔNG đi vào bản kể.</b> Tin nhắn của
/// <see cref="RenderUserMessage"/> được lưu dưới vai NGƯỜI DÙNG, nên mọi chữ trong đó được các tầng sau đọc
/// như lời họ: bộ chắt bản đồ bao phủ lấy làm <c>{nguồn: …}</c>, bộ chắt "điểm cần làm rõ" lấy làm một vế
/// mâu thuẫn, và BA đem ra chất vấn ở lượt kế. Nhưng thứ người dùng thật sự QUYẾT trên bảng chỉ là các Ô:
/// dòng nào giữ, thông tin nào cần lưu, nhập thế nào, danh sách lấy ở đâu, trạng thái nào có, dòng con
/// thuộc cha nào. <see cref="EntityMapRow.Description"/> thì do BA điền sẵn và nằm cạnh tên đối tượng như
/// một cái nhãn xám — họ bấm gửi mà không rà nó.
/// </para>
///
/// <para>
/// Ca thật (dự án JD Library 1). BA điền mô tả <i>"JD — Mô tả công việc được Manager tạo, kiểm tra, verify
/// và approve trước khi dùng để gán cho nhân viên"</i>, trong khi chính người dùng đã kể ở khung chat và đã
/// tự tay rà ở BẢNG LUỒNG rằng HRBP verify rồi HoD của Manager approve. Câu ấy quay về trong lượt mang tên
/// người dùng, và lượt kế BA hỏi <i>"luồng nào đúng với thực tế ạ?"</i> — bắt họ phân xử một mâu thuẫn giữa
/// lời họ và lời BA, đúng thứ mà mục "Hai vế phải cùng là lời NGƯỜI DÙNG" của prompt chat cấm. Thiệt hại
/// không dừng ở một lượt hỏng: mục tồn đọng sinh ra từ đó khiến <see cref="CoveragePendingGuard"/> hạ ba
/// dòng bản đồ («Đối tượng người dùng &amp; vai trò», «Chức năng &amp; luồng nghiệp vụ chính», «Vòng đời
/// &amp; trạng thái») xuống <c>[MỘT PHẦN]</c> và KHÓA cổng "Write Requirement" — ở đúng lượt mà mọi nhóm
/// vừa đủ. Và nếu người dùng chọn nhầm vế của BA thì luồng bốn mắt do chính họ kể bị lật, mọi tầng sau tin.
/// </para>
///
/// <para>
/// Mô tả vẫn được LƯU (nó là văn xuôi cho mục <c>## 8. Data Model Summary</c> của AI Design Spec), chỉ
/// không được đóng dấu là lời người dùng ở bất kỳ đường nào — xem <see cref="RenderConfirmedBlock"/>. Cùng
/// lỗi và cùng cách chặn với ô "việc của màn" ở <see cref="ScreenScopeMapBuilder"/>.
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

    /// <summary>
    /// Trần số giá trị của một danh sách NHẬP TẠI CHỖ. Ý nghĩa của "nhập tại chỗ" là danh sách chỉ có vài
    /// giá trị cố định — dài hơn ngần này thì nó là một danh mục thật, tức thuộc về "ứng dụng tự quản lý"
    /// với một màn hình riêng, chứ không phải một ô người dùng ngồi gõ lại mỗi lần bảng bày ra.
    /// </summary>
    public const int MaxOptionsPerField = 10;

    /// <summary>Ít hơn ngần này trạng thái thì không phải vòng đời — xem ghi chú class.</summary>
    public const int MinStatesForLifecycle = 2;

    /// <summary>
    /// Trần cho ô "mỗi cha có bao nhiêu dòng con". Không phải giới hạn kỹ thuật mà là phép thử tỉnh táo: một
    /// con số lớn hơn thế nghĩa là model gõ nhầm ô, hoặc dòng con thật ra là một đối tượng độc lập.
    /// </summary>
    public const int MaxChildRowCount = 100;

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
                // Quan hệ cha-con chỉ kiểm được khi đã biết TRỌN bảng (cha có thể đứng sau con), nên ở đây
                // chỉ chở giá trị thô qua; NormalizeParents ngay dưới mới là chỗ áp ba chốt chặn.
                ParentEntity = Clip((row.ParentEntity ?? string.Empty).Trim(), MaxTextChars),
                MinRows = row.MinRows,
                MaxRows = row.MaxRows,
                Included = !respectSelection || row.Included,
                // LUẬT BẰNG CHỨNG — cờ suông không khóa được dòng nào. Xem PermissionGrant.Locked.
                Locked = evidence.Length > 0,
                Evidence = evidence,
                AddedByUser = addedByUser
            });

            if (result.Count >= MaxRows)
                break;
        }

        NormalizeParents(result);
        return result;
    }

    /// <summary>
    /// Ba chốt chặn của quan hệ cha-con, áp SAU khi đã biết trọn bảng (cha có thể đứng sau con trong danh
    /// sách model trả về). Cả ba đều <b>hạ dòng về hồ sơ độc lập</b> chứ không loại dòng: một quan hệ sai là
    /// một ô điền sai, còn nuốt cả dòng là làm biến mất một đối tượng người dùng đã tích.
    ///
    /// <list type="number">
    ///   <item><b>Cha phải TỒN TẠI và còn được giữ</b> trong chính bảng này. Tên bịa (hoặc trỏ vào một dòng
    ///   người dùng vừa bỏ tích) sẽ thành một quan hệ mà <c>## 8. Data Model Summary</c> dựng ra rồi treo
    ///   lơ lửng. Cùng luật với <c>ScreenScopeMapBuilder.MatchScreen</c>: chính tả lấy của BẢNG, không của
    ///   model.</item>
    ///   <item><b>Không tự làm cha của mình.</b></item>
    ///   <item><b>Tối đa MỘT cấp</b> — cha của một dòng con thì không được có cha nữa. POC dựng grid lồng
    ///   grid là thứ không ai duyệt nổi, và người dùng nghiệp vụ không mô hình hoá ba tầng. Luật này xét
    ///   trên trạng thái TRƯỚC khi sửa và áp đúng một lượt, nên nó tất định và làm mọi chu trình tự vỡ:
    ///   A→B→C giữ B→C và cắt A (giữ được cấp gần gốc nhất), còn A→B→A thì cả hai cùng có cha-có-cha nên
    ///   cả hai về độc lập.</item>
    /// </list>
    /// </summary>
    private static void NormalizeParents(List<EntityMapRow> rows)
    {
        // Ảnh chụp TRƯỚC khi sửa: luật "một cấp" phải đọc lời khai gốc của CẢ bảng, nếu không thứ tự duyệt
        // quyết định ai được giữ quan hệ — cùng một bảng gửi hai lần cho hai kết quả khác nhau.
        var claimed = rows.Select(r => Normalize(r.ParentEntity)).ToList();

        var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Included)
                indexByKey.TryAdd(Normalize(rows[i].Entity), i);
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (claimed[i].Length == 0)
                continue;

            if (!indexByKey.TryGetValue(claimed[i], out var parent) || parent == i || claimed[parent].Length > 0)
            {
                rows[i].ParentEntity = string.Empty;
                continue;
            }

            // Chính tả của BẢNG, không của model: hai cách viết cho cùng một đối tượng thì mọi tầng sau
            // tưởng là hai đối tượng.
            rows[i].ParentEntity = rows[parent].Entity;
            NormalizeChildRowCount(rows[i]);
        }

        // Số dòng con chỉ có nghĩa dưới một quan hệ. Rơi lại trên một hồ sơ độc lập, nó đọc như một ràng
        // buộc về số bản ghi của cả ứng dụng — thứ chưa ai từng hỏi.
        foreach (var row in rows.Where(r => r.ParentEntity.Length == 0))
        {
            row.MinRows = null;
            row.MaxRows = null;
        }
    }

    /// <summary>
    /// Ô "mỗi cha có bao nhiêu dòng": bỏ giá trị âm/quá trần, và ĐỔI CHỖ khi min &gt; max thay vì bỏ một
    /// trong hai — gõ nhầm thứ tự hai ô là ca thường gặp nhất, và đổi chỗ giữ được cả hai con số họ đã gõ.
    /// </summary>
    private static void NormalizeChildRowCount(EntityMapRow row)
    {
        row.MinRows = Clamp(row.MinRows);
        row.MaxRows = Clamp(row.MaxRows);

        if (row.MinRows.HasValue && row.MaxRows.HasValue && row.MinRows > row.MaxRows)
            (row.MinRows, row.MaxRows) = (row.MaxRows, row.MinRows);

        static int? Clamp(int? value)
            => value is null || value < 0 || value > MaxChildRowCount ? null : value;
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
    ///
    /// <para>
    /// Tiêu đề khối nói "đã được NGƯỜI DÙNG CHỐT", nên mọi dòng dưới nó được ba bên đọc như quyết định của
    /// người dùng. Câu <see cref="EntityMapRow.Description"/> KHÔNG phải quyết định của ai — nó do BA điền
    /// sẵn lúc bày bảng (xem ghi chú class) — nên nó đứng ở một dòng RIÊNG có gắn xuất xứ, không nối vào
    /// dòng tên đối tượng. Nối vào là đúng cách một câu văn xuôi của BA trở thành bằng chứng: bộ chắt bản
    /// đồ bao phủ trích nó làm <c>{nguồn: …}</c> và bước sinh spec đọc nó như một ràng buộc đã chốt.
    /// </para>
    /// </summary>
    public static string? RenderConfirmedBlock(string? json)
    {
        var rows = Parse(json).Where(r => r.Included).ToList();
        if (rows.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng đối tượng nghiệp vụ đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại) ---");
        sb.AppendLine("Mỗi đối tượng: thông tin cần lưu, rồi vòng đời trạng thái kèm ĐIỀU KIỆN chuyển vào.");

        // Câu dặn chỉ xuất hiện khi có ít nhất một dòng mô tả — không có mô tả nào mà vẫn dặn thì đó là một
        // dòng lệnh nói về thứ không tồn tại trong khối, và mỗi dòng thừa ở đây đi kèm MỌI lượt chat sau.
        if (rows.Any(r => !string.IsNullOrWhiteSpace(r.Description)))
            sb.AppendLine("Dòng \"mô tả\" là câu CHÍNH BẠN đặt lúc bày bảng, KHÔNG phải lời người dùng: đừng "
                + "trích nó làm bằng chứng, đừng lấy nó làm một vế mâu thuẫn với điều họ nói, và thấy nó lệch "
                + "với hội thoại thì tự sửa im lặng chứ không hỏi.");

        foreach (var row in rows)
        {
            sb.AppendLine($"* {row.Entity}");

            if (!string.IsNullOrWhiteSpace(row.Description))
                sb.AppendLine($"  - mô tả (BA tự đặt, chưa ai rà): {row.Description}");

            // Quan hệ đứng NGAY dưới tên đối tượng chứ không xuống cuối khối: nó đổi nghĩa của mọi dòng
            // phía dưới (các "thông tin" ở đây là cột của MỘT DÒNG con, không phải của một hồ sơ).
            if (row.ParentEntity.Length > 0)
                sb.AppendLine($"  - {RenderParent(row)}");

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

            // Ô chọn chưa chốt được nguồn: CÙNG hình dạng ngoại lệ và cùng lý do. Bảng đã chốt không có
            // nghĩa là mọi ô trong nó đã có câu trả lời, và một ô chọn không nói được danh sách lấy ở đâu
            // sẽ đi thẳng vào spec dưới dạng một dropdown không ai dựng được. Ngoại lệ ghi ngay tại dòng
            // của đối tượng chứ không gom xuống cuối khối, để BA hỏi đúng chỗ.
            foreach (var pending in PendingFieldNotes(row))
                sb.AppendLine($"  - {pending} ⇒ hỏi nốt (ngoại lệ của luật \"đừng hỏi lại\").");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Tin nhắn mà TRÌNH DUYỆT gửi tiếp vào khung chat sau khi bảng đã lưu — cùng khuôn hai bước với các
    /// bảng khác, và soạn ở server vì cùng lý do: bản kể phải khớp đúng bản đã lưu.
    ///
    /// <para>
    /// Bản kể chở đúng những Ô người dùng vừa quyết, KHÔNG chở câu mô tả BA điền sẵn: tin nhắn này được lưu
    /// dưới vai NGƯỜI DÙNG, nên chữ nào lọt vào đây cũng thành lời của họ ở mọi tầng phía sau. Xem ghi chú
    /// class cho ca thật.
    /// </para>
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
            sb.AppendLine($"{row.Entity}:");

            if (row.ParentEntity.Length > 0)
                sb.AppendLine($"- {RenderParent(row)}");

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

            foreach (var pending in PendingFieldNotes(row))
                sb.AppendLine($"- {pending}.");
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

        // Các danh mục "ứng dụng tự quản lý" phải được GỌI TÊN, vì đây là chỗ bảng đối tượng đẻ ra thứ
        // không thuộc về nó: mỗi danh mục như vậy là một MÀN HÌNH nữa của ứng dụng. Người dùng chỉ chọn
        // "app tự quản lý" trong một ô nhỏ ở bảng này, nên nếu bản kể không nói ra thì họ không có cách nào
        // biết mình vừa đặt hàng thêm ba màn hình — mà chính bản kể này là thứ mọi tầng chắt lọc phía sau
        // đọc. Việc đưa chúng vào phạm vi màn hình do ConfirmEntityMapUseCase làm.
        var managed = ManagedListScreens(rows);
        if (managed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Các danh mục ứng dụng tự quản lý (mỗi danh mục cần một màn hình quản lý riêng): "
                + string.Join(", ", managed) + ".");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Các MÀN HÌNH mà bảng đối tượng đẻ ra: mỗi thông tin còn tích, kiểu CHỌN, nguồn "ứng dụng tự quản lý"
    /// là một danh mục mà ứng dụng phải có màn hình CRUD riêng để quản lý.
    ///
    /// <para>
    /// <b>Vì sao nó phải chảy ra khỏi bảng này.</b> Một quyết định "danh sách này do ứng dụng tự quản lý"
    /// nằm lại trong cột <c>EntityMap</c> sẽ không có màn hình nào trong <c>## 6. Screens To Generate</c> và
    /// không có DÒNG nào trong bảng phân quyền — tức mặc nhiên "không ai được xem" một màn hình mà người
    /// dùng vừa đặt hàng. Đường ra là <c>Project.PlannedScope</c>, và chính hàm này là lý do thứ tự phụ
    /// thuộc của buổi phỏng vấn đặt bảng đối tượng TRƯỚC bảng màn hình
    /// (<c>luồng → đối tượng → báo cáo → màn hình → phân quyền → thông báo</c>): gieo trước lần bày đầu thì
    /// các màn hình danh mục là những dòng bình thường của bảng màn hình, người dùng tích/bỏ tích ngay tại
    /// đó. Thứ tự cũ (màn hình trước) buộc chúng đi vào bằng đường MỞ LẠI của <c>ScreenScopeGate</c> —
    /// người dùng đã chốt "đây là toàn bộ màn hình" rồi mới thấy danh sách dài thêm, và
    /// <c>RequirementConflictService</c> bắn một mâu thuẫn cho đúng cái phạm vi vừa đổi. Xem
    /// <c>InterviewTableGate</c> và <c>ConfirmEntityMapUseCase</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Hình dạng tên: <c>"&lt;Danh mục&gt; Catalog"</c>.</b> Hai ràng buộc cùng lúc, và chỉ hậu tố mới thoả
    /// được cả hai. (1) Tên phải đọc được như MỘT MÀN HÌNH, vì cột "Màn hình" của bảng màn hình chỉ được
    /// chứa màn hình — một mục tên là <c>"OrgUnit"</c> trần trùng nguyên văn tên đối tượng ở bảng ngay
    /// trước đó, nên nó được rà như một màn hình mà không ai biết nó làm gì. (2) Tên phải NGẮN và bằng
    /// TIẾNG ANH, vì nó đi thẳng ra <c>## 6. Screens To Generate</c> rồi thành NHÃN MỤC MENU của bản demo —
    /// Developer chép nguyên văn, không dịch, không rút gọn (xem <c>Prompts/Developer/poc-preview.v1.md</c>),
    /// nên một chữ dẫn tiếng Việt ở đây là một chữ dẫn tiếng Việt trên sidebar. Luật đầy đủ ở
    /// <c>docs/requirement-flow.md</c>, mục "Tên màn hình là nhãn menu của bản demo".
    /// </para>
    /// </summary>
    public static List<string> ManagedListScreens(IEnumerable<EntityMapRow>? rows)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in (rows ?? Enumerable.Empty<EntityMapRow>())
                     .Where(r => r != null && r.Included)
                     .SelectMany(r => r.Fields)
                     .Where(f => f != null && f.Used
                         && EntityFieldInput.IsChoice(f.Input)
                         && f.Source == EntityFieldSource.App
                         && !string.IsNullOrWhiteSpace(f.Name)))
        {
            // Cùng một danh mục thường xuất hiện ở nhiều đối tượng (OrgUnit của JD và của nhân viên) —
            // gieo hai lần là bắt người dùng rà hai dòng cho cùng một màn hình.
            if (seen.Add(Normalize(field.Name)))
                result.Add($"{field.Name.Trim()} Catalog");
        }

        return result;
    }

    /// <summary>Bản đọc từ JSON đã lưu của <see cref="ManagedListScreens(IEnumerable{EntityMapRow})"/>.</summary>
    public static List<string> ManagedListScreens(string? entityMapJson) => ManagedListScreens(Parse(entityMapJson));

    /// <summary>
    /// Tên các đối tượng người dùng đã GIỮ ở bảng đã chốt — bộ đối chiếu cho mọi bảng sau muốn trỏ về một
    /// đối tượng (<see cref="ReportMapBuilder"/> dùng nó cho ô "lấy số từ"). Đối tượng đã bỏ tích KHÔNG có
    /// mặt: trỏ một báo cáo vào thứ người dùng vừa loại là dựng lại đúng thứ họ vừa đóng.
    /// </summary>
    public static List<string> EntityNames(string? entityMapJson)
        => Parse(entityMapJson)
            .Where(r => r.Included && !string.IsNullOrWhiteSpace(r.Entity))
            .Select(r => r.Entity.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ==== chuẩn hoá từng phần ====

    /// <summary>
    /// Quan hệ cha-con ở dạng một câu — chủ ngữ là CHA vì đó là cách người dùng nói ("mỗi JD có 5 trách
    /// nhiệm"), và vì mọi tầng đọc sau đó cần biết bản ghi nào chứa danh sách này.
    /// </summary>
    private static string RenderParent(EntityMapRow row)
    {
        var count = (row.MinRows, row.MaxRows) switch
        {
            (int min, int max) when min == max => $" {min}",
            (int min, int max) => $" {min}–{max}",
            (int min, null) => $" từ {min}",
            (null, int max) => $" tối đa {max}",
            _ => string.Empty
        };
        return $"là các DÒNG của \"{row.ParentEntity}\" — mỗi \"{row.ParentEntity}\" có{count} dòng như thế";
    }

    /// <summary>
    /// MỘT thông tin, ở dạng cả người dùng lẫn model đọc được. Phần trong ngoặc vuông chỉ xuất hiện khi có
    /// gì để nói: một ô gõ tay không bắt buộc là ca THƯỜNG GẶP NHẤT, và dán "gõ tay" vào từng dòng của một
    /// bảng mười hai dòng chỉ làm loãng đúng những dòng có ràng buộc thật.
    /// </summary>
    private static string RenderField(EntityFieldNote field)
    {
        var head = string.IsNullOrWhiteSpace(field.Meaning)
            ? field.Name.Trim()
            : $"{field.Name.Trim()} ({field.Meaning.Trim()})";

        var constraints = ConstraintLabel(field);
        return constraints.Length > 0 ? $"{head} [{constraints}]" : head;
    }

    /// <summary>
    /// Các RÀNG BUỘC của một thông tin, gộp thành một chuỗi — rỗng khi chẳng có gì để nói (ô gõ tay không
    /// bắt buộc, ca thường gặp nhất). Công khai vì bản xuất hội thoại phải kể đúng bộ ràng buộc mà các khối
    /// ngữ cảnh kể: người chấm đối chiếu hai bản đó với nhau, và hai cách diễn đạt cho cùng một ô là đúng
    /// thứ khiến họ đi tìm một khác biệt không tồn tại.
    /// </summary>
    public static string ConstraintLabel(EntityFieldNote field)
    {
        var notes = new List<string>();
        if (field.Required)
            notes.Add("bắt buộc");

        switch (field.Input)
        {
            case EntityFieldInput.Number:
                notes.Add("nhập số");
                break;
            case EntityFieldInput.Date:
                notes.Add("nhập ngày");
                break;
            case EntityFieldInput.Auto:
                notes.Add(string.IsNullOrWhiteSpace(field.Rule)
                    ? "ứng dụng tự sinh, người dùng không nhập"
                    : $"ứng dụng tự sinh theo quy tắc {field.Rule.Trim()}, người dùng không nhập");
                break;
            case EntityFieldInput.ChoiceOne:
            case EntityFieldInput.ChoiceMany:
                notes.Add(field.Input == EntityFieldInput.ChoiceOne
                    ? "chọn 1 giá trị"
                    : "chọn nhiều giá trị");
                notes.Add(RenderFieldSource(field));
                break;
        }

        return string.Join(" · ", notes);
    }

    /// <summary>
    /// Vế "danh sách lấy ở đâu" của một ô chọn. Ba ca CHƯA CHỐT được gọi tên thẳng thay vì bỏ trống, vì đây
    /// là chuỗi chữ mà cả hội thoại lẫn spec đọc: một ô chọn không nói được danh sách lấy ở đâu thì POC
    /// không dựng nổi cái dropdown ấy, và im lặng ở đây là cách chắc chắn nhất để không ai hỏi nốt.
    /// </summary>
    private static string RenderFieldSource(EntityFieldNote field) => field.Source switch
    {
        EntityFieldSource.Inline when field.Options.Count > 0
            => "danh sách cố định: " + string.Join(", ", field.Options),
        EntityFieldSource.Inline => "danh sách cố định nhưng CHƯA nêu giá trị nào",
        EntityFieldSource.App => "danh sách do ứng dụng tự quản lý (cần một màn hình quản lý riêng)",
        EntityFieldSource.External when !string.IsNullOrWhiteSpace(field.SourceSystem)
            => $"lấy từ {field.SourceSystem.Trim()}",
        EntityFieldSource.External => "lấy từ hệ thống khác nhưng CHƯA rõ hệ thống nào",
        _ => "CHƯA rõ danh sách lấy ở đâu"
    };

    /// <summary>
    /// Các ô CHƯA CHỐT của một đối tượng, mỗi ca một câu — nguyên liệu cho ngoại lệ của luật "đừng hỏi lại"
    /// ở <see cref="RenderConfirmedBlock"/> và cho phần tự khai của <see cref="RenderUserMessage"/>.
    ///
    /// <para>
    /// Vì sao chúng KHÔNG chặn nút gửi, khác bảng thông báo: ở đó dòng khóa không có checkbox nên người
    /// dùng không có đường nào thoát ra ngoài việc điền, còn ở đây họ luôn bỏ tích được cả dòng. Dựng thêm
    /// một cổng từ chối để đổi lấy một câu hỏi mà BA hỏi được ở lượt sau là dựng thêm một chỗ kẹt.
    /// </para>
    /// </summary>
    private static List<string> PendingFieldNotes(EntityMapRow row)
    {
        var notes = new List<string>();
        var fields = row.Fields.Where(f => f.Used && EntityFieldInput.IsChoice(f.Input)).ToList();

        var noSource = fields.Where(f => f.Source.Length == 0).Select(f => f.Name.Trim()).ToList();
        if (noSource.Count > 0)
            notes.Add("chưa rõ danh sách lấy ở đâu: " + string.Join(", ", noSource));

        var noSystem = fields
            .Where(f => f.Source == EntityFieldSource.External && string.IsNullOrWhiteSpace(f.SourceSystem))
            .Select(f => f.Name.Trim()).ToList();
        if (noSystem.Count > 0)
            notes.Add("chưa rõ lấy từ hệ thống nào: " + string.Join(", ", noSystem));

        var noOptions = fields
            .Where(f => f.Source == EntityFieldSource.Inline && f.Options.Count == 0)
            .Select(f => f.Name.Trim()).ToList();
        if (noOptions.Count > 0)
            notes.Add("chưa nêu các giá trị của danh sách: " + string.Join(", ", noOptions));

        return notes;
    }

    /// <summary>
    /// Chuẩn hoá danh sách giá trị nhập tại chỗ: bỏ rỗng, bỏ trùng (theo bản chuẩn hoá, nhưng GIỮ chính tả
    /// người dùng gõ — chữ ấy lên thẳng dropdown của POC), cắt theo <see cref="MaxOptionsPerField"/>.
    /// </summary>
    private static List<string> NormalizeOptions(IEnumerable<string>? proposed)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var option in proposed ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(option))
                continue;

            var value = Clip(option.Trim(), MaxTextChars);
            if (!seen.Add(Normalize(value)))
                continue;

            result.Add(value);
            if (result.Count >= MaxOptionsPerField)
                break;
        }

        return result;
    }

    // Ô "báo cho ai" KHÔNG có mặt ở đây: người nhận thông báo có bảng riêng, và hai khối ngữ cảnh cùng
    // nói về một quyết định là cách chắc chắn nhất để BA hỏi lại thứ người dùng vừa chốt ở bảng kia.
    private static string RenderState(EntityLifecycleState state)
    {
        var entry = string.IsNullOrWhiteSpace(state.EntryCondition) ? string.Empty : $" khi {state.EntryCondition.Trim()}";
        return $"\"{state.State.Trim()}\"{entry}";
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

            var used = !respectSelection || field.Used;
            var input = EntityFieldInput.Normalize(field.Input);
            var isChoice = EntityFieldInput.IsChoice(input);
            var source = isChoice ? EntityFieldSource.Normalize(field.Source) : string.Empty;

            result.Add(new EntityFieldNote
            {
                Name = name,
                Meaning = meaning,
                Used = used,
                // Ba luật ép cờ bắt buộc về false, và cả ba là "ô này không có nghĩa" chứ không phải một
                // lựa chọn bị bác: thông tin KHÔNG lưu thì không có ô nào để bắt buộc, và thông tin ứng
                // dụng TỰ SINH thì người dùng không hề nhập — bắt buộc nhập nó là một ràng buộc mà POC
                // dựng ra sẽ chặn đúng cái biểu mẫu nó vừa dựng.
                Required = used && input != EntityFieldInput.Auto && field.Required,
                Input = input,
                Source = source,
                // Ba ô phụ chỉ sống dưới đúng một nhánh của hai trục. Cắt ở đây chứ không để UI ẩn đi:
                // người dùng đổi kiểu từ "chọn nhiều" sang "gõ tay" thì các giá trị họ gõ lúc trước còn
                // nằm trong payload, và một danh sách treo dưới một ô gõ tay sẽ được cả spec lẫn POC đọc
                // như thật.
                Options = source == EntityFieldSource.Inline
                    ? NormalizeOptions(field.Options)
                    : new List<string>(),
                SourceSystem = source == EntityFieldSource.External
                    ? Clip((field.SourceSystem ?? string.Empty).Trim(), MaxTextChars)
                    : string.Empty,
                Rule = input == EntityFieldInput.Auto
                    ? Clip((field.Rule ?? string.Empty).Trim(), MaxTextChars)
                    : string.Empty
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
                EntryCondition = Clip((state.EntryCondition ?? string.Empty).Trim(), MaxTextChars)
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
