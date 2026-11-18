using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using static ICOGenerator.Services.Requirements.RequirementText;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng màn hình" — phạm vi màn hình của ứng dụng kèm các chức năng trên từng màn, do BA
/// tự soạn từ hội thoại (<see cref="ScreenScopeDraftService"/>) rồi thành nền cho mọi thứ phía sau: bảng
/// phân quyền đứng trên đúng các chức năng này, và AI Design Spec dựng đúng các màn hình này (xem
/// <see cref="ScreenScopeRow"/>).
///
/// <para>
/// Cùng ba chốt chặn tất định với <see cref="PermissionMatrixBuilder"/> — và chúng quan trọng hơn ở đây,
/// vì bảng này là thứ bảng phân quyền sẽ đứng lên:
/// </para>
/// <list type="bullet">
///   <item><b>Màn hình bịa.</b> Mọi dòng phải khớp một mục của danh sách cho phép (phạm vi đã chắt từ hội
///   thoại và từ bảng báo cáo), và luôn lấy lại đúng chữ của danh sách đó chứ không chữ của model. Ngoại lệ
///   DUY NHẤT: dòng sinh ra để nhận một bước luồng ĐÃ CHỐT mà không màn hình nào đang có phụ trách nổi
///   (bảo lãnh là chính phép kiểm tất định ngay dưới — xem <see cref="ApplyPlacements"/>).</item>
///   <item><b>Màn hình bị bỏ quên.</b> Mục phạm vi model không nhắc tới vẫn được BỔ SUNG vào cuối bảng.
///   Ngoại lệ DUY NHẤT: mục đã được một dòng khai là gộp vào mình qua <see cref="ScreenScopeRow.Covers"/> —
///   xem <see cref="CoveredScopeItems"/>.</item>
///   <item><b>Bước luồng không chức năng nào phụ trách.</b> Chốt chặn RIÊNG của bảng này và là lý do
///   <see cref="ScreenFunction.FlowSteps"/> tồn tại — xem <see cref="UncoveredActions"/>. Bắt được lỗ hổng
///   thì BA tự QUYẾT: xếp chỗ cho bước ấy (<see cref="ApplyPlacements"/>) hoặc xác định nó làm ngoài phần
///   mềm (<see cref="ResolveOutsideSteps"/>) — không câu hỏi nào ném ngược sang người dùng.</item>
/// </list>
///
/// <para>
/// <b>Cờ <see cref="ScreenScopeRow.ConfirmedByUser"/> nay nghĩa là "đã CHỐT".</b> Tên cờ giữ nguyên vì nó
/// đã nằm trong JSON lưu ở DB; người đóng dấu nay là lượt soạn của BA (<see cref="Settle"/>), không phải
/// người dùng bấm gửi bảng. Ý nghĩa với các tầng sau thì không đổi: mục mang dấu là phạm vi đã đứng yên,
/// mục chưa mang dấu là phần vừa lộ ra và đang chờ lượt soạn kế tiếp.
/// </para>
/// </summary>
public static class ScreenScopeMapBuilder
{
    /// <summary>Trần số màn hình. Cùng trần với số dòng phạm vi mà PermissionMatrixBuilder chấp nhận.</summary>
    public const int MaxRows = 40;

    /// <summary>Trần số bước luồng gắn cho MỘT chức năng — nhiều hơn là dấu hiệu model dán cả luồng vào một dòng.</summary>
    public const int MaxFlowStepsPerFunction = 6;

    /// <summary>
    /// Trần số chức năng của MỘT màn hình. Một màn hình 20 chức năng không phải một màn hình được mô tả kỹ
    /// mà là hai màn hình bị gộp làm một — và bảng dài quá thì người dùng thôi đọc, tức là mất đúng thứ
    /// tính năng này đi mua.
    /// </summary>
    public const int MaxFunctionsPerScreen = 12;

    /// <summary>Trần số mục phạm vi mà MỘT màn hình được khai là đã gộp vào mình.</summary>
    public const int MaxCoversPerScreen = 8;

    private const int MaxTextChars = 200;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Bảng cuối cùng cho lượt BA SOẠN phạm vi: các dòng ĐANG LƯU đứng trước và luôn thắng, đề xuất TƯƠI của
    /// model chỉ lấp vào phần chưa chốt, dòng bịa/trùng bị bỏ, mọi màn hình chưa được nhắc tới vẫn được bổ
    /// sung (trừ mục đã được gộp), và thứ tự luôn theo <paramref name="allowedScreens"/>. Trả rỗng khi danh
    /// sách cho phép trống.
    ///
    /// <para>
    /// <b>Vì sao dòng đang lưu phải là một tham số RIÊNG chứ không nối chung vào
    /// <paramref name="proposed"/>.</b> Chỉ dòng đang lưu mới được mang
    /// <see cref="ScreenScopeRow.ConfirmedByUser"/>: structured output buộc model điền đủ trường, nên một
    /// model điền <c>true</c> cho có sẽ tự đóng dấu chốt lên một mục chưa ai soạn — và cờ ấy là thứ quyết
    /// định cổng còn mở hay đóng. Cùng luật với <c>included</c>: mọi dòng và mọi chức năng ra TÍCH SẴN bất
    /// kể model trả gì, vì loại một màn hình không phải chỗ model tự phủ nhận đề xuất của mình.
    /// </para>
    ///
    /// <para>
    /// <b>Model được lấp vào đâu.</b> Dòng CHƯA CHỐT thì có — mục vừa lộ ra mới chỉ có cái tên (và cùng lắm
    /// vài chức năng), còn ô "việc của màn" và ô "phục vụ bước nào" là phần việc của BA ở đúng lượt này. Dòng
    /// ĐÃ CHỐT thì KHÔNG, trừ đúng một đường: GỘP một mục chưa chốt vào nó (<see cref="Absorb"/>). Việc của
    /// màn và danh sách chức năng đã đứng yên, để model đoán lại là đổi phạm vi dưới chân bảng phân quyền.
    /// Và mục đang lưu chưa chốt mà model không đứng tên nhưng khai gộp vào dòng khác thì được GỘP thật —
    /// chức năng nó chở đi theo sang dòng nhận (<see cref="MoveFunctions"/>).
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> Build(
        IEnumerable<ScreenScopeRow>? stored,
        IEnumerable<ScreenScopeRow>? proposed,
        IReadOnlyList<string> allowedScreens)
        => BuildCore(stored, proposed, allowedScreens);

    /// <summary>
    /// Bảng để LƯU sau lượt soạn: đóng dấu CHỐT cho mọi dòng và mọi chức năng của <paramref name="drafted"/>,
    /// rồi giữ lại các BIA của bảng đang lưu — dòng và chức năng đã bị loại (<c>Included = false</c>, từ
    /// thời bảng còn bày ra cho người dùng bỏ tích). Bia không được gieo vào lượt soạn
    /// (<see cref="SeedRows"/> lọc chúng ra), nên ghi đè thẳng là xoá trí nhớ về những gì đã loại, và lượt
    /// chắt lọc kế tiếp gặp lại đúng cái tên đó sẽ ghép lại vào bảng như một mục mới tinh.
    ///
    /// <para>
    /// Mục còn chờ trong bảng đang lưu mà lượt soạn đã khai GỘP vào một dòng khác (<see cref="ScreenScopeRow.Covers"/>)
    /// thì KHÔNG được giữ: nó không bao giờ là một dòng riêng, và giữ nó ở trạng thái chưa chốt là để cổng
    /// mở lại mãi. <see cref="Merge"/> đọc lời khai gộp nên cũng không dựng lại nó.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> Settle(string? screenScopeJson, IReadOnlyList<ScreenScopeRow> drafted)
    {
        var result = drafted.ToList();
        foreach (var row in result)
        {
            row.ConfirmedByUser = true;
            foreach (var function in row.Functions)
                function.ConfirmedByUser = true;
        }

        var stored = Parse(screenScopeJson);
        if (stored.Count == 0)
            return result;

        var shown = new HashSet<string>(result.Select(r => Normalize(r.Screen)), StringComparer.Ordinal);
        foreach (var row in result)
        {
            var previous = stored.FirstOrDefault(r => Normalize(r.Screen) == Normalize(row.Screen));
            if (previous == null)
                continue;

            var names = new HashSet<string>(row.Functions.Select(f => Normalize(f.Name)), StringComparer.Ordinal);
            foreach (var function in previous.Functions.Where(f => !f.Included))
            {
                if (names.Add(Normalize(function.Name)) && row.Functions.Count < MaxFunctionsPerScreen)
                    row.Functions.Add(function);
            }
        }

        result.AddRange(stored.Where(r => !r.Included && !shown.Contains(Normalize(r.Screen))));
        return result;
    }

    private static List<ScreenScopeRow> BuildCore(
        IEnumerable<ScreenScopeRow>? stored,
        IEnumerable<ScreenScopeRow>? proposed,
        IReadOnlyList<string> allowedScreens)
    {
        var screens = CleanScreens(allowedScreens);
        // Danh sách cho phép rỗng ⇒ không có gì để soạn.
        if (screens.Count == 0)
            return new List<ScreenScopeRow>();

        var byScreen = new Dictionary<string, ScreenScopeRow>(StringComparer.Ordinal);
        var fromStore = new HashSet<string>(StringComparer.Ordinal);
        // Màn hình mà MODEL đề xuất thành một dòng của chính nó (dòng mới, hoặc lấp vào dòng đang lưu). Chỉ
        // mục đang lưu CHƯA CHỐT mà model KHÔNG đứng tên mới gộp được vào dòng khác — xem FoldableScopeItems.
        var proposedOn = new HashSet<string>(StringComparer.Ordinal);

        var sources = (stored ?? Enumerable.Empty<ScreenScopeRow>()).Select(r => (Row: r, Trusted: true))
            .Concat((proposed ?? Enumerable.Empty<ScreenScopeRow>()).Select(r => (Row: r, Trusted: false)));

        foreach (var (row, trusted) in sources)
        {
            if (row == null)
                continue;

            var screen = MatchScreen(row.Screen, screens);
            if (screen == null)
                continue;

            if (byScreen.TryGetValue(screen, out var existing))
            {
                // Dòng ĐANG LƯU đứng trước nên nó luôn thắng. Đề xuất của model lấp vào một dòng CHƯA CHỐT;
                // với dòng ĐÃ CHỐT thì nó chỉ được GỘP thêm phần phạm vi mới — xem ghi chú của Build.
                if (!trusted && fromStore.Contains(screen))
                {
                    proposedOn.Add(screen);
                    if (existing.ConfirmedByUser)
                        Absorb(existing, row, PendingStoredItems(stored, screens));
                    else
                        Enrich(existing, row);
                }
                continue;
            }

            // chữ của DANH SÁCH CHO PHÉP, không phải chữ của model
            byScreen[screen] = NewRow(row, screen, trusted);
            if (trusted)
                fromStore.Add(screen);
            else
                proposedOn.Add(screen);
        }

        // Ai đứng tên một mục phạm vi: mọi dòng model đề xuất, mọi dòng đã chốt — và dòng đang lưu CHƯA CHỐT
        // chỉ khi model cũng đề xuất nó. Dòng chưa chốt mà model không đứng tên nhưng lại khai GỘP vào một
        // dòng khác là đúng thứ lượt soạn sinh ra để dọn: một "Tính năng …" lượt chắt lọc đưa lên làm màn hình.
        var owners = byScreen
            .Where(kv => !fromStore.Contains(kv.Key) || kv.Value.ConfirmedByUser || proposedOn.Contains(kv.Key))
            .Select(kv => kv.Value)
            .ToList();
        var covered = CoveredScopeItems(byScreen.Values, owners, screens);

        var result = new List<ScreenScopeRow>();
        foreach (var screen in screens)
        {
            if (covered.Contains(Normalize(screen)))
            {
                // Mục này đã nằm trong cột chức năng của một màn hình khác — xem CoveredScopeItems. Dòng chưa
                // chốt của nó (nếu có) không được mang theo chức năng nó đang chở vào hư không: chuyển sang
                // dòng đã gộp nó.
                if (byScreen.TryGetValue(screen, out var folded))
                    MoveFunctions(folded, byScreen.Values.FirstOrDefault(r => r != folded && r.Covers.Any(c => MatchScreen(c, new List<string> { screen }) != null)));
                continue;
            }

            if (byScreen.TryGetValue(screen, out var found))
                result.Add(found);
            else
                // Màn hình model bỏ quên: vẫn phải có mặt, TÍCH SẴN — bỏ hẳn là làm nó biến mất khỏi mọi tầng
                // sau (bảng phân quyền, spec) trong khi hội thoại đã đặt hàng nó.
                result.Add(new ScreenScopeRow { Screen = screen, Included = true });

            if (result.Count >= MaxRows)
                break;
        }

        return result;
    }

    /// <summary>
    /// Lấp đề xuất của model vào một dòng CHƯA CHỐT: chỉ THÊM, không bao giờ ghi đè. Câu "việc của màn"
    /// chỉ được điền khi đang trống, chức năng và lời khai gộp chỉ được thêm mục chưa có.
    /// </summary>
    private static void Enrich(ScreenScopeRow existing, ScreenScopeRow incoming)
    {
        if (existing.Purpose.Length == 0)
            existing.Purpose = Clip((incoming.Purpose ?? string.Empty).Trim(), MaxTextChars);

        foreach (var function in CleanFunctions(incoming.Functions, trusted: false))
        {
            if (existing.Functions.Count >= MaxFunctionsPerScreen)
                break;
            if (existing.Functions.Any(f => Normalize(f.Name) == Normalize(function.Name)))
                continue;
            existing.Functions.Add(function);
        }

        foreach (var item in CleanCovers(incoming.Covers, existing.Screen))
        {
            if (existing.Covers.Count >= MaxCoversPerScreen)
                break;
            if (existing.Covers.Any(c => Normalize(c) == Normalize(item)))
                continue;
            existing.Covers.Add(item);
        }
    }

    /// <summary>
    /// Đề xuất của model cho một dòng ĐÃ CHỐT: chỉ được GỘP phần phạm vi MỚI vào nó — các mục đang lưu chưa
    /// chốt mà dòng này khai trong <see cref="ScreenScopeRow.Covers"/> (<paramref name="pendingItems"/>), kèm
    /// chức năng mới model mang theo cho phần ấy. Không khai gộp mục mới nào ⇒ không đụng gì: việc của màn,
    /// chức năng và ô "phục vụ bước nào" đã chốt giữ nguyên, vì bảng phân quyền đứng trên chúng.
    ///
    /// <para>
    /// Vì sao phải có đường này: phạm vi trôi sau lần chốt thường là một CHỨC NĂNG của một màn đã chốt mà lượt
    /// chắt lọc đưa lên thành dòng riêng (<i>"Tính năng xuất Excel từ Training Plan"</i>). Hồi bảng còn bày ra,
    /// người dùng bỏ tích được dòng thừa đó; nay không ai rà, nên chỗ đúng của nó — một chức năng trên màn
    /// đã chốt — chỉ tới được bằng lời khai gộp của lượt soạn.
    /// </para>
    /// </summary>
    private static void Absorb(ScreenScopeRow existing, ScreenScopeRow incoming, IReadOnlyCollection<string> pendingItems)
    {
        var absorbed = CleanCovers(incoming.Covers, existing.Screen)
            .Where(c => pendingItems.Contains(Normalize(c)))
            .ToList();
        if (absorbed.Count == 0)
            return;

        foreach (var item in absorbed)
        {
            if (existing.Covers.Count >= MaxCoversPerScreen)
                break;
            if (existing.Covers.All(c => Normalize(c) != Normalize(item)))
                existing.Covers.Add(item);
        }

        foreach (var function in CleanFunctions(incoming.Functions, trusted: false))
        {
            if (existing.Functions.Count >= MaxFunctionsPerScreen)
                break;
            if (existing.Functions.All(f => Normalize(f.Name) != Normalize(function.Name)))
                existing.Functions.Add(function);
        }
    }

    /// <summary>Các mục của bảng đang lưu còn CHƯA CHỐT, chuẩn hoá theo chữ của danh sách cho phép.</summary>
    private static HashSet<string> PendingStoredItems(IEnumerable<ScreenScopeRow>? stored, IReadOnlyList<string> screens)
        => new(
            (stored ?? Enumerable.Empty<ScreenScopeRow>())
                .Where(r => r != null && !r.ConfirmedByUser)
                .Select(r => MatchScreen(r.Screen, screens))
                .Where(m => m != null)
                .Select(m => Normalize(m!)),
            StringComparer.Ordinal);

    /// <summary>
    /// Chuyển các chức năng của một dòng vừa bị GỘP sang dòng đã gộp nó (chỉ thêm tên chưa có, trong trần).
    /// Không tìm được dòng nhận ⇒ không làm gì.
    /// </summary>
    private static void MoveFunctions(ScreenScopeRow folded, ScreenScopeRow? into)
    {
        if (into == null)
            return;

        foreach (var function in folded.Functions)
        {
            if (into.Functions.Count >= MaxFunctionsPerScreen)
                break;
            if (into.Functions.All(f => Normalize(f.Name) != Normalize(function.Name)))
                into.Functions.Add(function);
        }
    }

    /// <summary>
    /// Một dòng đã chuẩn hoá. <paramref name="trusted"/> = dòng đến từ BẢNG ĐANG LƯU: chỉ nó mới được chở
    /// <see cref="ScreenScopeRow.ConfirmedByUser"/> (và cờ tự-thêm của dự án cũ) qua, dòng model đề xuất luôn
    /// ra <c>false</c>.
    /// </summary>
    private static ScreenScopeRow NewRow(ScreenScopeRow source, string screen, bool trusted)
    {
        return new ScreenScopeRow
        {
            Screen = screen,
            Purpose = Clip((source.Purpose ?? string.Empty).Trim(), MaxTextChars),
            Functions = CleanFunctions(source.Functions, trusted),
            Covers = CleanCovers(source.Covers, screen),
            Included = true,
            AddedByUser = trusted && source.AddedByUser,
            ConfirmedByUser = trusted && source.ConfirmedByUser
        };
    }

    /// <summary>
    /// Các mục phạm vi đã được một dòng khai là GỘP vào mình, chuẩn hoá sẵn để đối chiếu.
    ///
    /// <para>
    /// Một mục chỉ được coi là đã gộp khi KHÔNG dòng nào trong <paramref name="owners"/> đứng tên nó: dòng
    /// luôn thắng lời khai gộp. Không có luật đó thì model chỉ cần khai bừa tên một màn hình thật vào
    /// <see cref="ScreenScopeRow.Covers"/> của dòng khác là màn hình ấy biến mất khỏi bảng trong im lặng.
    /// Ngoại lệ có chủ ý nằm ở người gọi: dòng đang lưu CHƯA CHỐT mà model không đứng tên thì không tính là
    /// đứng tên — đó là mục lượt soạn có quyền quyết.
    /// </para>
    /// </summary>
    private static HashSet<string> CoveredScopeItems(
        IEnumerable<ScreenScopeRow> rows, IEnumerable<ScreenScopeRow> owners, IReadOnlyList<string> screens)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var owned = new HashSet<string>(owners.Select(r => Normalize(r.Screen)), StringComparer.Ordinal);

        foreach (var row in rows)
        {
            foreach (var item in row.Covers)
            {
                var match = MatchScreen(item, screens);
                if (match == null)
                    continue;

                var key = Normalize(match);
                if (!owned.Contains(key))
                    claimed.Add(key);
            }
        }

        return claimed;
    }

    /// <summary>Đọc JSON bảng màn hình đã lưu (cột DB hoặc payload client). null/rỗng/hỏng ⇒ mảng rỗng.</summary>
    public static List<ScreenScopeRow> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<ScreenScopeRow>();

        try
        {
            var rows = JsonSerializer.Deserialize<List<ScreenScopeRow>>(json, ReadOptions)
                ?? new List<ScreenScopeRow>();
            return rows.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Screen)).ToList();
        }
        catch
        {
            return new List<ScreenScopeRow>();
        }
    }

    /// <summary>
    /// Dự án này đã có ít nhất một dòng đã CHỐT chưa (đã qua một lượt soạn). Cột khác null KHÔNG đồng nghĩa
    /// "đã chốt": lượt chắt lọc và bảng báo cáo ghép mục vào đó từ trước lượt soạn đầu tiên.
    /// </summary>
    public static bool IsConfirmed(string? json) => Parse(json).Any(r => r.ConfirmedByUser);

    /// <summary>
    /// Bảng còn mục nào CHƯA CHỐT không — điều kiện mở của <see cref="ScreenScopeGate"/>.
    /// </summary>
    public static bool HasPending(string? json)
    {
        var rows = Parse(json);
        return rows.Any(r => r.Included && !r.ConfirmedByUser)
            || rows.Any(r => r.Included && r.Functions.Any(f => f.Included && !f.ConfirmedByUser));
    }

    /// <summary>
    /// Các MÀN HÌNH chưa chốt: dòng còn tích mà chưa qua lượt soạn. Dùng để gọi tên phần mới cho lượt soạn
    /// LẠI (<see cref="ScreenScopeDraftService"/>).
    /// </summary>
    public static List<string> PendingScreens(string? json)
        => Parse(json).Where(r => r.Included && !r.ConfirmedByUser).Select(r => r.Screen.Trim()).ToList();

    /// <summary>
    /// Các CHỨC NĂNG chưa chốt trên những màn hình ĐÃ chốt, kể kèm tên màn.
    ///
    /// <para>
    /// Chỉ tính trên dòng đã chốt để không kể hai lần: chức năng của một màn hình chưa chốt đã nằm trong
    /// <see cref="PendingScreens"/> rồi — cả cái màn hình ấy là mới.
    /// </para>
    /// </summary>
    public static List<string> PendingFunctions(string? json)
        => Parse(json)
            .Where(r => r.Included && r.ConfirmedByUser)
            .SelectMany(r => r.Functions
                .Where(f => f.Included && !f.ConfirmedByUser)
                .Select(f => $"{f.Name.Trim()} (ở {r.Screen.Trim()})"))
            .ToList();

    /// <summary>
    /// PHẠM VI MÀN HÌNH THẬT SỰ của dự án — nguồn dòng cho bảng phân quyền, cho danh sách cho phép của lượt
    /// soạn phạm vi, và cho mục <c>## 6. Screens To Generate</c> của spec.
    ///
    /// <para>
    /// Là mọi dòng CÒN TÍCH, chốt rồi hay chưa. Mục chưa chốt phải có mặt vì buổi phỏng vấn còn tiếp tục
    /// sau lúc bảng được chốt; mục đã BỎ TÍCH (dự án cũ) thì không bao giờ quay lại — dòng bia ở lại trong
    /// bảng chính là thứ bảo đảm điều đó (xem <see cref="Merge"/>).
    /// </para>
    /// </summary>
    public static List<string> EffectiveScreens(string? screenScopeJson)
        => Parse(screenScopeJson).Where(r => r.Included).Select(r => r.Screen.Trim()).ToList();

    /// <summary>
    /// GHÉP phần phạm vi vừa lộ ra (<see cref="ScopeAddition"/> của lượt chắt lọc, hoặc màn hình do một bảng
    /// khác gieo sang) vào bảng đang lưu. Trả <c>null</c> khi không có gì mới — người gọi đừng ghi DB.
    ///
    /// <para>
    /// <b>Chỉ THÊM, không bao giờ sửa hay bớt.</b> Không dòng nào bị xoá, không cờ tích nào bị đổi, không
    /// câu "việc của màn" nào đang có bị viết đè, và không dấu chốt nào bị gỡ. Đây là bất biến chở cả tính
    /// năng: lượt chắt lọc là một lời gọi LLM chạy ở hậu kỳ sau lưng người dùng, nên mọi thứ nó chạm được
    /// phải là thứ mất đi cũng không xoá được quyết định của ai. Mục mới vào ở trạng thái CHƯA CHỐT, và chỗ
    /// nó được quyết là lượt soạn phạm vi kế tiếp (<see cref="ScreenScopeDraftService"/>).
    /// </para>
    ///
    /// <para>
    /// Ba cửa đóng lại, và cả ba đều là lý do bản cũ phải dựng một phép so tập hợp mỗi lượt: mục trùng tên
    /// một dòng đã có ⇒ chỉ xét phần chức năng; mục trùng một dòng người dùng đã BỎ TÍCH ⇒ bỏ hẳn (bia);
    /// mục đã được một dòng khai là GỘP vào mình (<see cref="ScreenScopeRow.Covers"/>) ⇒ cũng bỏ hẳn, nếu
    /// không thì mỗi lượt nó lại mọc lại thành một dòng trắng ngay dưới chỗ vừa được gộp.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow>? Merge(string? screenScopeJson, IEnumerable<ScopeAddition>? additions)
    {
        if (additions == null)
            return null;

        var rows = Parse(screenScopeJson);
        var changed = false;

        foreach (var addition in additions)
        {
            if (addition == null)
                continue;

            var name = Clip((addition.Screen ?? string.Empty).Trim(), MaxTextChars);
            if (name.Length == 0)
                continue;

            var matched = MatchScreen(name, rows.Select(r => r.Screen).ToList());
            var row = matched == null ? null : rows.First(r => r.Screen == matched);

            if (row == null)
            {
                if (rows.Any(r => r.Covers.Any(c => MatchScreen(name, new List<string> { c }) != null)))
                    continue;
                if (rows.Count >= MaxRows)
                    continue;

                row = new ScreenScopeRow
                {
                    Screen = name,
                    Purpose = Clip((addition.Purpose ?? string.Empty).Trim(), MaxTextChars),
                    Included = true
                };
                rows.Add(row);
                changed = true;
            }
            else if (!row.Included)
            {
                // BIA. Màn hình này đã bị loại; dựng lại nó là mở lại thứ đã đóng.
                continue;
            }

            foreach (var raw in addition.Functions ?? new List<string>())
            {
                var function = Clip((raw ?? string.Empty).Trim(), MaxTextChars);
                if (function.Length == 0 || row.Functions.Count >= MaxFunctionsPerScreen)
                    continue;
                // So khớp tên chức năng bằng CHỨA-NHAU sau chuẩn hoá, cùng phép so với tên màn hình: model
                // chép lại một chức năng đã có bằng chữ của nó là chuyện thường, và mỗi lần như thế là một
                // mục chưa chốt giả — tức một lượt soạn lại (một lời gọi LLM) không có việc gì để làm.
                if (MatchScreen(function, row.Functions.Select(f => f.Name).ToList()) != null)
                    continue;

                row.Functions.Add(new ScreenFunction { Name = function, Included = true });
                changed = true;
            }
        }

        return changed ? rows : null;
    }

    /// <summary>
    /// Các dòng ĐANG LƯU để gieo cho lượt soạn phạm vi: chỉ màn hình CÒN TÍCH, và trong mỗi màn chỉ chức
    /// năng CÒN TÍCH — chốt rồi hay chưa đều vào, vì cả hai đều đang là phạm vi của ứng dụng.
    ///
    /// <para>
    /// Không có hạt giống này thì lần soạn lại là một lượt phá hoại: <see cref="Build"/> dựng bảng từ đề
    /// xuất TƯƠI của model, nên mọi thứ đã chốt ở lần trước — việc của từng màn, danh sách chức năng, ô
    /// "phục vụ bước nào" — bị thay bằng bản model vừa đoán lại, và bảng phân quyền đứng trên các chức năng
    /// ấy mất chỗ đứng. Lọc theo cờ tích là phần còn lại của cùng một luật: <see cref="Build"/> trả mọi dòng
    /// ở trạng thái TÍCH SẴN, nên đưa cả phần đã loại vào hạt giống là bật lại đúng thứ đã tắt. Phần bị lọc
    /// ra không mất: <see cref="Settle"/> giữ nó lại lúc lưu.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> SeedRows(string? screenScopeJson)
    {
        var rows = Parse(screenScopeJson).Where(r => r.Included).ToList();
        foreach (var row in rows)
            row.Functions = row.Functions.Where(f => f.Included).ToList();

        return rows;
    }

    /// <summary>
    /// Các BƯỚC LUỒNG đã chốt mà KHÔNG chức năng nào trong bảng nhận phụ trách — phép kiểm tất định của
    /// tính năng, chạy bằng code chứ không bằng một lời gọi LLM nữa.
    ///
    /// <para>
    /// Vì sao nó đáng có: hai danh sách này đọc riêng đều "đạt" — bảng luồng đầy đủ, bảng màn hình đầy đủ —
    /// còn chỗ hỏng nằm ở mối nối giữa chúng, đúng loại lỗi đắt nhất của cả dây chuyền. Một bước không ai
    /// phụ trách nghĩa là hoặc người dùng sẽ không có chỗ nào để làm bước đó, hoặc bước đó không có thật.
    /// Cả hai đều phải hỏi, và hỏi ngay lúc bảng còn trên màn hình rẻ hơn hẳn hỏi lại ở POC.
    /// </para>
    ///
    /// <para>
    /// Chỉ đếm chức năng CÒN TÍCH của màn hình CÒN TÍCH: bỏ tích một chức năng là bỏ luôn phần việc nó gánh,
    /// nên bước của nó phải lập tức hiện ra là chưa ai làm. Đó là phần mà bản cũ — gắn bước ở cấp màn hình —
    /// không nói được: người dùng bỏ đúng chức năng chở bước đó mà cả bảng vẫn báo "đủ".
    /// </para>
    ///
    /// <para>
    /// So khớp bằng CHỨA-NHAU sau chuẩn hoá chứ không khớp chính xác: người dùng sửa ô "phục vụ bước" bằng
    /// lời của họ, và một phép so nguyên văn sẽ báo động giả ở gần như mọi dòng — mà một cảnh báo luôn sai
    /// thì lần thứ hai không ai đọc nữa.
    /// </para>
    /// </summary>
    public static List<string> UncoveredActions(IReadOnlyList<ScreenScopeRow> rows, string? flowMapJson)
        => Split(rows, FlowMapBuilder.IncludedActions(flowMapJson)).Uncovered;

    /// <summary>
    /// Trong <paramref name="actions"/>, những bước ĐANG được một chức năng còn tích phụ trách — cùng phép
    /// so với <see cref="UncoveredActions"/>, chiều ngược lại. Dùng để nhận ra người dùng vừa giao cho một
    /// chức năng cái bước mà BA đã xếp là "làm ngoài phần mềm".
    /// </summary>
    public static List<string> CoveredActions(IReadOnlyList<ScreenScopeRow> rows, IReadOnlyList<string> actions)
        => Split(rows, actions).Covered;

    private static (List<string> Covered, List<string> Uncovered) Split(IReadOnlyList<ScreenScopeRow> rows, IReadOnlyList<string> actions)
    {
        var result = (Covered: new List<string>(), Uncovered: new List<string>());
        if (actions.Count == 0)
            return result;

        var covered = rows
            .Where(r => r.Included)
            .SelectMany(r => r.Functions)
            .Where(f => f.Included)
            .SelectMany(f => f.FlowSteps)
            .Select(Normalize)
            .Where(s => s.Length > 0)
            .ToList();

        foreach (var action in actions)
        {
            var key = Normalize(action);
            if (key.Length == 0)
                continue;

            var isCovered = covered.Any(c => c.Contains(key, StringComparison.Ordinal) || key.Contains(c, StringComparison.Ordinal));
            (isCovered ? result.Covered : result.Uncovered).Add(action);
        }

        return result;
    }

    /// <summary>
    /// Nhánh "LÀM NGOÀI PHẦN MỀM" của lượt xếp chỗ: nhận các bước model khai là không thuộc ứng dụng và
    /// kéo chúng về đúng chữ của bảng luồng. Cùng chốt chặn với <see cref="ApplyPlacements"/> — chỉ bước
    /// nằm trong <paramref name="uncoveredSteps"/> mới được nhận, nên lượt này không thành đường để model
    /// gạt một bước ĐÃ có chức năng phụ trách ra khỏi ứng dụng.
    /// </summary>
    public static List<ScreenStepOutside> ResolveOutsideSteps(
        IEnumerable<ScreenStepOutside>? declared,
        IReadOnlyList<string> uncoveredSteps)
    {
        var result = new List<ScreenStepOutside>();
        foreach (var item in declared ?? Enumerable.Empty<ScreenStepOutside>())
        {
            var key = Normalize(item?.Step ?? string.Empty);
            if (key.Length == 0)
                continue;

            var match = uncoveredSteps.FirstOrDefault(step =>
            {
                var wanted = Normalize(step);
                return wanted.Length > 0
                       && (wanted.Contains(key, StringComparison.Ordinal) || key.Contains(wanted, StringComparison.Ordinal));
            });
            if (match == null || result.Any(r => string.Equals(r.Step, match, StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(new ScreenStepOutside
            {
                Step = match.Trim(),
                Reason = Clip((item!.Reason ?? string.Empty).Trim(), MaxTextChars)
            });
        }

        return result;
    }

    /// <summary>
    /// XẾP CHỖ cho các bước mồ côi: nhận lời xếp chỗ của lượt <c>ScreenStepPlacementService</c> và ghi
    /// chúng vào bảng, để phạm vi được chốt đã KÍN thay vì kèm một câu hỏi ngược người dùng.
    ///
    /// <para>
    /// <b>Vì sao lượt này tồn tại.</b> <see cref="UncoveredActions"/> bắt đúng lỗi cần bắt, nhưng phần còn
    /// lại thì trước đây đẩy hết sang người dùng: dòng nhắc dưới bảng bảo họ tự điền bước vào ô của "chức
    /// năng phù hợp" hoặc tự phát hiện ra một màn hình còn thiếu. Đó là phần việc của BA, và người dùng
    /// nghiệp vụ vừa rà xong một bảng mười mấy màn hình không có cơ sở nào để làm nó. Ca thật (JD Library
    /// 2): bước *"Xem danh sách nhân viên trực tiếp dưới quyền"* — bước 4 của luồng chính người dùng đã tự
    /// tay chốt — hiện ra dưới bảng như một câu đố, trong khi chỗ đúng của nó là một chức năng trên màn
    /// <c>JD Assignment</c> mà chính bảng đó đang có.
    /// </para>
    ///
    /// <para>
    /// <b>Ba luật của lượt xếp chỗ, và cả ba đều là chốt chặn:</b>
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Chỉ lấp, không sửa.</b> Lời xếp chỗ nào không trỏ vào một bước trong
    ///   <paramref name="uncoveredSteps"/> đều bị bỏ. Không có luật này thì một lượt sinh ra để vá lỗ hổng
    ///   trở thành một đường vòng cho model viết lại cả bảng — kể cả phần đã chốt ở lần trước (bảng này
    ///   soạn LẠI được, xem <see cref="SeedRows"/>).</item>
    ///   <item><b>Chỉ THÊM, không bao giờ bớt.</b> Không dòng nào bị xóa, không cờ tích nào bị đổi, không
    ///   câu "việc của màn" nào đang có bị viết đè. Bước về một chức năng đã có ⇒ gắn thêm vào ô "phục vụ
    ///   bước" của nó; không có chức năng nào tên vậy ⇒ một chức năng MỚI ở cuối màn.</item>
    ///   <item><b>Màn hình MỚI được nhận — ngoại lệ DUY NHẤT của chốt chặn "màn hình bịa"</b>, và nó hẹp
    ///   đúng bằng lý do sinh ra nó: dòng mới phải mang một bước mà NGƯỜI DÙNG đã chốt ở bảng luồng và
    ///   không màn hình nào đang có phụ trách. Chốt chặn kia dựng để chặn model rải thêm màn hình cho đủ
    ///   bộ; ở đây thì thứ chặn nó là phép kiểm tất định vừa chạy, không phải một danh sách cho phép.</item>
    /// </list>
    ///
    /// <para>
    /// Cờ <see cref="ScreenScopeRow.AddedByUser"/> của dòng mới là <c>false</c>: đây là thiết kế của BA.
    /// </para>
    ///
    /// <para>
    /// Danh sách trả về là danh sách MỚI, nhưng các dòng bên trong là chính các object của
    /// <paramref name="rows"/> đã được ghi thêm chức năng/bước — giữ lại tham chiếu bảng cũ không cho bạn
    /// một ảnh chụp "trước khi xếp". Chỗ cần so trước/sau thì chụp riêng thứ mình cần.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> ApplyPlacements(
        IReadOnlyList<ScreenScopeRow> rows,
        IEnumerable<ScreenStepPlacement>? placements,
        IReadOnlyList<string> uncoveredSteps)
    {
        var result = rows.ToList();
        if (placements == null || result.Count == 0)
            return result;

        // Chỉ những bước phép kiểm vừa gọi tên mới được xếp chỗ. So bằng CHỨA-NHAU sau chuẩn hoá, cùng
        // phép so với UncoveredActions: model chép lại bước bằng chữ của nó là chuyện thường, và một phép
        // so nguyên văn sẽ bỏ đúng những lời xếp chỗ đúng.
        var wanted = uncoveredSteps
            .Select(step => (Text: step.Trim(), Key: Normalize(step)))
            .Where(w => w.Key.Length > 0)
            .ToList();
        if (wanted.Count == 0)
            return result;

        var screens = result.Select(r => r.Screen).ToList();

        foreach (var placement in placements)
        {
            if (placement == null)
                continue;

            var key = Normalize(placement.Step ?? string.Empty);
            if (key.Length == 0)
                continue;

            // Ô "phục vụ bước" nhận chữ của BẢNG LUỒNG, không chữ model vừa gõ lại. Hai lý do, và cả hai
            // đều là chuyện đúng-sai chứ không phải thẩm mỹ: UncoveredActions so bảng này với chính danh
            // sách ấy nên một bản diễn đạt lại là một lần báo động giả chực chờ, còn người dùng thì đang
            // đọc đúng các bước mình vừa tự tay rà ở bảng trước — thấy chúng hiện ra bằng chữ khác là mất
            // đường đối chiếu.
            var match = wanted
                .Where(w => w.Key.Contains(key, StringComparison.Ordinal) || key.Contains(w.Key, StringComparison.Ordinal))
                .Select(w => w.Text)
                .FirstOrDefault();
            if (match == null)
                continue;

            var step = Clip(match, MaxTextChars);

            var function = Clip((placement.Function ?? string.Empty).Trim(), MaxTextChars);
            if (function.Length == 0)
                continue;

            var screenName = Clip((placement.Screen ?? string.Empty).Trim(), MaxTextChars);
            if (screenName.Length == 0)
                continue;

            var matched = MatchScreen(screenName, screens);
            var row = matched == null ? null : result.FirstOrDefault(r => r.Screen == matched);
            if (row == null)
            {
                // MÀN HÌNH MỚI. Trần MaxRows vẫn áp: một bảng dài quá thì người dùng thôi đọc, và mất phần
                // họ đọc là mất đúng thứ cả bảng này sinh ra để lấy.
                if (result.Count >= MaxRows)
                    continue;

                row = new ScreenScopeRow
                {
                    Screen = screenName,
                    Purpose = Clip((placement.Purpose ?? string.Empty).Trim(), MaxTextChars),
                    Included = true
                };
                result.Add(row);
                screens.Add(screenName);
            }

            AttachStep(row, function, step);
        }

        return result;
    }

    /// <summary>
    /// Gắn <paramref name="step"/> vào chức năng <paramref name="function"/> của <paramref name="row"/> —
    /// chức năng đã có thì gắn thêm bước, chưa có thì thêm một chức năng mới ở cuối màn. Chỉ THÊM: cờ tích
    /// và tên của phần đang có không bị đụng tới.
    /// </summary>
    private static void AttachStep(ScreenScopeRow row, string function, string step)
    {
        var key = Normalize(function);
        var existing = row.Functions.FirstOrDefault(f => Normalize(f.Name) == key);
        if (existing != null)
        {
            // Đã phụ trách bước này rồi (chỉ khác chữ) ⇒ không nhân đôi ô "phục vụ bước".
            var stepKey = Normalize(step);
            if (existing.FlowSteps.Any(s => Normalize(s) == stepKey)
                || existing.FlowSteps.Count >= MaxFlowStepsPerFunction)
            {
                return;
            }

            existing.FlowSteps.Add(step);
            return;
        }

        if (row.Functions.Count >= MaxFunctionsPerScreen)
            return;

        row.Functions.Add(new ScreenFunction
        {
            Name = function,
            FlowSteps = new List<string> { step },
            Included = true
        });
    }

    /// <summary>
    /// Khối ngữ cảnh gắn vào MỌI lượt chat sau khi phạm vi đã chốt, vào lượt distill bản đồ bao phủ, vào
    /// lượt soạn bảng phân quyền và vào prompt sinh AI Design Spec. Trả null khi chưa chốt.
    ///
    /// <para>
    /// Danh sách chức năng ở đây là thứ bảng phân quyền phải đứng lên: dòng của bảng đó là một cặp
    /// (màn hình × chức năng).
    /// </para>
    ///
    /// <para>
    /// <b>Khối nói rõ đây là THIẾT KẾ CỦA BA, không phải lời người dùng</b> — để lượt distill không lấy nó làm
    /// căn cứ cho một nhóm nào, và để BA không đối chất người dùng bằng nó. Lệnh dành riêng cho lượt chat
    /// (không hỏi người dùng chỗ đặt chức năng) nằm ở <c>BAChatPromptBlocks.ConfirmedScreenScope</c>, đứng
    /// ngay trước khối này trong ngữ cảnh chat; khối này còn đi vào prompt sinh spec, nơi lệnh đó vô nghĩa.
    /// </para>
    /// </summary>
    public static string? RenderConfirmedBlock(string? json)
    {
        // CHỈ phần mang dấu chốt: phần chưa chốt là phạm vi vừa lộ ra, lượt soạn kế tiếp mới quyết nó.
        var rows = Parse(json).Where(r => r.ConfirmedByUser).ToList();
        if (rows.Count == 0)
            return null;

        var kept = rows.Where(r => r.Included).ToList();
        var dropped = rows.Where(r => !r.Included).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Phạm vi màn hình đã CHỐT (BA soạn từ hội thoại — phạm vi màn hình của ứng dụng) ---");
        sb.AppendLine("Đây là các màn hình của ứng dụng và các chức năng trên từng màn — thiết kế BA soạn từ điều "
            + "người dùng đã kể, KHÔNG phải lời người dùng.");

        // Câu "việc của màn" là văn xuôi BA đặt, không phải lời người dùng, nên nó đứng riêng có gắn xuất xứ
        // thay vì nối vào dòng tên màn hình.
        if (kept.Any(r => !string.IsNullOrWhiteSpace(r.Purpose)))
            sb.AppendLine("Dòng \"việc của màn\" là câu CHÍNH BẠN đặt, KHÔNG phải lời người dùng: đừng trích nó "
                + "làm bằng chứng và đừng lấy nó làm một vế mâu thuẫn với điều họ nói.");

        foreach (var row in kept)
        {
            sb.AppendLine($"* {row.Screen}");

            if (!string.IsNullOrWhiteSpace(row.Purpose))
                sb.AppendLine($"  - việc của màn (BA tự đặt): {row.Purpose}");

            foreach (var function in row.Functions.Where(f => f.Included && f.ConfirmedByUser))
            {
                var steps = function.FlowSteps.Count > 0
                    ? $" (phục vụ bước: {string.Join("; ", function.FlowSteps)})"
                    : string.Empty;
                sb.AppendLine($"  - chức năng: {function.Name}{steps}");
            }

            var droppedFunctions = row.Functions.Where(f => !f.Included && f.ConfirmedByUser).Select(f => f.Name).ToList();
            if (droppedFunctions.Count > 0)
                sb.AppendLine($"  - chức năng đã LOẠI (đừng dựng, đừng nhắc lại): {string.Join(", ", droppedFunctions)}");

            if (row.Covers.Count > 0)
                sb.AppendLine($"  - đã gộp vào màn này: {string.Join(", ", row.Covers)}");
        }

        if (dropped.Count > 0)
            sb.AppendLine("Màn hình đã LOẠI (đừng dựng, đừng nhắc lại): "
                + string.Join(", ", dropped.Select(r => r.Screen)) + ".");

        return sb.ToString().TrimEnd();
    }

    // ==== chuẩn hoá từng phần ====

    private static List<string> CleanScreens(IReadOnlyList<string>? screens)
    {
        var result = new List<string>();
        if (screens == null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in screens)
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

    // Chức năng KHÔNG có tên thì không có gì để phân quyền: bỏ.
    private static List<ScreenFunction> CleanFunctions(IEnumerable<ScreenFunction>? proposed, bool trusted)
    {
        var result = new List<ScreenFunction>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var function in proposed ?? Enumerable.Empty<ScreenFunction>())
        {
            if (function == null)
                continue;

            var name = Clip((function.Name ?? string.Empty).Trim(), MaxTextChars);
            if (name.Length == 0 || !seen.Add(Normalize(name)))
                continue;

            result.Add(new ScreenFunction
            {
                Name = name,
                FlowSteps = CleanFlowSteps(function.FlowSteps),
                Included = true,
                ConfirmedByUser = trusted && function.ConfirmedByUser
            });

            if (result.Count >= MaxFunctionsPerScreen)
                break;
        }
        return result;
    }

    private static List<string> CleanFlowSteps(IEnumerable<string>? proposed)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in proposed ?? Enumerable.Empty<string>())
        {
            var step = (raw ?? string.Empty).Trim();
            if (step.Length == 0 || !seen.Add(Normalize(step)))
                continue;

            result.Add(Clip(step, MaxTextChars));
            if (result.Count >= MaxFlowStepsPerFunction)
                break;
        }
        return result;
    }

    private static List<string> CleanCovers(IEnumerable<string>? proposed, string screen)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Normalize(screen) };
        foreach (var raw in proposed ?? Enumerable.Empty<string>())
        {
            var item = (raw ?? string.Empty).Trim();
            if (item.Length == 0 || !seen.Add(Normalize(item)))
                continue;

            result.Add(Clip(item, MaxTextChars));
            if (result.Count >= MaxCoversPerScreen)
                break;
        }
        return result;
    }

    // Cùng phép ghép tên màn hình với PermissionMatrixBuilder (khớp chính xác trước, rồi cho phép một bên
    // chứa bên kia khi model rút gọn tên), và cùng ngưỡng độ dài để những mẩu quá ngắn không dính vào mọi
    // mục. Mơ hồ (nhiều mục cùng khớp) ⇒ bỏ hẳn: gán bừa là đặt cả một màn hình lên nhầm dòng.
    //
    // Merge dùng lại chính hàm này cho TÊN CHỨC NĂNG, và đó là cố ý: hai chỗ đều đang hỏi cùng một câu
    // ("mục model vừa nêu có phải là mục đã có trong bảng không") và một phép so lỏng hơn ở cấp chức năng
    // sẽ đẻ ra mục chưa chốt giả mỗi lượt chat.
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

        var matches = screens.Where(s =>
        {
            var normalized = Normalize(s);
            return normalized.Contains(value, StringComparison.Ordinal)
                || (normalized.Length >= MinContainsLength && value.Contains(normalized, StringComparison.Ordinal));
        }).ToList();

        return matches.Count == 1 ? matches[0] : null;
    }
}
