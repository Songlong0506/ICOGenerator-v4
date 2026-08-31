using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng màn hình" — danh sách màn hình dự kiến của ứng dụng kèm các chức năng trên từng
/// màn, để người dùng rà trước khi nó thành nền cho mọi thứ phía sau (xem <see cref="ScreenScopeRow"/>).
///
/// <para>
/// Cùng ba chốt chặn tất định với <see cref="PermissionMatrixBuilder"/> — và chúng quan trọng hơn ở đây,
/// vì bảng này là thứ bảng phân quyền sẽ đứng lên:
/// </para>
/// <list type="bullet">
///   <item><b>Màn hình bịa.</b> Mọi dòng phải khớp một mục của danh sách cho phép, và luôn lấy lại đúng chữ
///   của danh sách đó chứ không chữ của model. Lượt BÀY BẢNG đối chiếu với phạm vi đã chắt
///   (<see cref="Build"/>); đường GỬI đối chiếu với chính bảng server đã render (<see cref="Sanitize"/>) —
///   hai danh sách khác nhau, và trộn chúng là lỗi câm, xem <c>ConfirmScreenScopeUseCase</c>. Đúng HAI
///   ngoại lệ, cả hai đều có thứ khác đứng ra bảo lãnh: dòng người dùng TỰ THÊM bằng nút "thêm màn hình"
///   (chốt chặn này dựng để chặn model, không phải để chặn người dùng — xem
///   <see cref="ScreenScopeRow.AddedByUser"/>), và dòng sinh ra để nhận một bước luồng ĐÃ CHỐT mà không
///   màn hình nào đang có phụ trách nổi (bảo lãnh là chính phép kiểm tất định ngay dưới — xem
///   <see cref="ApplyPlacements"/>).</item>
///   <item><b>Màn hình bị bỏ quên.</b> Mục phạm vi model không nhắc tới vẫn được BỔ SUNG vào cuối bảng —
///   ở trạng thái TÍCH SẴN như mọi dòng khác, vì "BA quên nêu" không phải "người dùng đã loại". Bỏ nó đi
///   là ra một quyết định thay người dùng ở đúng chỗ họ không nhìn thấy để phản đối. Ngoại lệ DUY NHẤT:
///   mục đã được một dòng khai là gộp vào mình qua <see cref="ScreenScopeRow.Covers"/> — xem
///   <see cref="CoveredScopeItems"/>.</item>
///   <item><b>Bước luồng không chức năng nào phụ trách.</b> Chốt chặn RIÊNG của bảng này và là lý do
///   <see cref="ScreenFunction.FlowSteps"/> tồn tại — xem <see cref="UncoveredActions"/>. Bắt được lỗ hổng
///   thì BA tự XẾP CHỖ cho bước ấy trước khi bảng hiện ra (<see cref="ApplyPlacements"/>); dòng nhắc dưới
///   bảng chỉ còn là chỗ rơi cuối cùng, không còn là câu hỏi mặc định ném ngược sang người dùng.</item>
/// </list>
///
/// <para>
/// <b>Ô "việc của màn" không đi vào bản kể</b>, cùng luật và cùng lý do với ô mô tả của
/// <see cref="EntityMapBuilder"/> (ca thật ghi ở đó): <see cref="RenderUserMessage"/> được lưu dưới vai
/// NGƯỜI DÙNG, mà <see cref="ScreenScopeRow.Purpose"/> là văn xuôi BA điền sẵn và đọc như một cái nhãn xám
/// dưới tên màn. Quyết định của người dùng ở bảng này là các Ô: màn nào giữ, chức năng nào giữ, màn nào tự
/// thêm. Câu "việc của màn" đi cùng chuyến gửi chứ không được ai rà, nên nó không được đóng dấu thành lời
/// họ rồi quay lại làm bằng chứng hay làm một vế mâu thuẫn ở các lượt sau.
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
    /// Bảng cuối cùng cho lượt BA BÀY BẢNG: các dòng ĐANG LƯU đứng trước và luôn thắng, đề xuất TƯƠI của
    /// model chỉ lấp vào phần chưa ai rà, dòng bịa/trùng bị bỏ, mọi màn hình chưa được nhắc tới vẫn được bổ
    /// sung (trừ mục đã được gộp), và thứ tự luôn theo <paramref name="allowedScreens"/>. Trả rỗng khi danh
    /// sách cho phép trống — không có phạm vi thì bảng không có gì để hỏi.
    ///
    /// <para>
    /// <b>Vì sao dòng đang lưu phải là một tham số RIÊNG chứ không nối chung vào
    /// <paramref name="proposed"/>.</b> Chỉ dòng đang lưu mới được mang
    /// <see cref="ScreenScopeRow.ConfirmedByUser"/>: structured output buộc model điền đủ trường, nên một
    /// model điền <c>true</c> cho có sẽ tự đóng dấu chữ ký người dùng lên một màn hình họ chưa từng nhìn
    /// thấy — và cờ ấy là thứ quyết định cổng còn mở hay đóng. Tách hai nguồn là cách duy nhất để
    /// <see cref="BuildCore"/> biết dòng nào được phép chở cờ. Cùng luật với <c>included</c>: ở đây mọi
    /// dòng và mọi chức năng ra TÍCH SẴN bất kể model trả gì, vì cờ đó là chỗ NGƯỜI DÙNG loại một màn hình,
    /// không phải chỗ model tự phủ nhận đề xuất của mình.
    /// </para>
    ///
    /// <para>
    /// <b>Model được lấp vào đâu.</b> Dòng đã <see cref="ScreenScopeRow.ConfirmedByUser"/> thì KHÔNG: người
    /// dùng đã tự tay rà việc của màn và danh sách chức năng, để model đoán lại là xoá đúng phần đắt nhất
    /// của buổi phỏng vấn. Dòng còn CHỜ DUYỆT thì có — mục vừa lộ ra mới chỉ có cái tên (và cùng lắm vài
    /// chức năng), còn ô "việc của màn" và ô "phục vụ bước nào" là phần việc của BA ở đúng lượt này.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> Build(
        IEnumerable<ScreenScopeRow>? stored,
        IEnumerable<ScreenScopeRow>? proposed,
        IReadOnlyList<string> allowedScreens)
        => BuildCore(stored, proposed, allowedScreens, respectIncluded: false, acceptUserAdded: false);

    /// <summary>
    /// Bản chuẩn hoá cho dữ liệu ĐẾN TỪ TRÌNH DUYỆT. Server không tin bảng client gửi kể cả khi chính nó
    /// vừa render ra: tên màn hình vẫn phải khớp lại <paramref name="allowedScreens"/>. Khác
    /// <see cref="Build"/>: giữ đúng lựa chọn tích/bỏ tích của người dùng ở CẢ hai cấp (màn hình và chức
    /// năng), và NHẬN các dòng người dùng TỰ
    /// THÊM dù chúng không có trong <paramref name="allowedScreens"/> — xem
    /// <see cref="ScreenScopeRow.AddedByUser"/>. Dòng tự thêm xếp SAU CÙNG, đúng chỗ chúng đứng trên bảng.
    ///
    /// <para>
    /// Đây là ĐÚNG MỘT chỗ trong cả hệ thống đóng dấu <see cref="ScreenScopeRow.ConfirmedByUser"/>, và nó
    /// đóng dấu cho MỌI dòng, MỌI chức năng — kể cả phần bị bỏ tích. Bấm gửi là hành vi xác nhận cả bảng:
    /// dòng người dùng không đụng tới cũng là dòng họ đã đọc và giữ, còn dòng họ bỏ tích thì phải mang dấu
    /// để không lượt chắt lọc nào dựng nó lại được (xem <see cref="Merge"/>).
    /// </para>
    ///
    /// <para>
    /// <paramref name="allowedScreens"/> phải là danh sách màn hình của CHÍNH BẢNG SERVER ĐÃ RENDER, không
    /// phải bảng đang lưu đọc lại lúc gửi: giữa lúc bày bảng và lúc bấm gửi vẫn có thể có một lượt chat
    /// khác, và lượt đó ghép thêm được mục mới vào bảng (<see cref="Merge"/>). Xem
    /// <c>ConfirmScreenScopeUseCase</c>.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> Sanitize(IEnumerable<ScreenScopeRow>? submitted, IReadOnlyList<string> allowedScreens)
    {
        // Cờ TỰ THÊM là thứ DUY NHẤT của dòng còn sống qua đường gửi ngoài phần người dùng tự điền: nó là
        // một sự thật về nguồn gốc của dòng mà RenderUserMessage còn phải kể lại.
        var rows = BuildCore(null, submitted, allowedScreens, respectIncluded: true, acceptUserAdded: true);
        foreach (var row in rows)
        {
            row.ConfirmedByUser = true;
            foreach (var function in row.Functions)
                function.ConfirmedByUser = true;
        }
        return rows;
    }

    /// <summary>
    /// Bảng để LƯU sau khi người dùng gửi: <paramref name="submitted"/> đè lên bảng đang lưu, nhưng những
    /// dòng đang lưu mà lượt bày vừa rồi KHÔNG mang ra hỏi thì được GIỮ NGUYÊN ở cuối.
    ///
    /// <para>
    /// Thứ được giữ lại chính là các BIA: dòng người dùng đã bỏ tích ở lần chốt trước không có mặt trong
    /// bảng bày lại (<see cref="SeedRows"/> lọc chúng ra, và danh sách cho phép cũng không chứa chúng), nên
    /// ghi đè thẳng là xoá sạch trí nhớ về những gì họ đã loại. Lượt chắt lọc kế tiếp gặp lại đúng cái tên
    /// đó trong hội thoại sẽ coi nó là một mục MỚI tinh và ghép lại vào bảng — mở lại thứ người dùng vừa
    /// đóng, đúng lỗi mà cả bộ bảng sinh ra để chặn. Cùng lý do cho chức năng bị bỏ tích, nên phép giữ chạy
    /// ở CẢ HAI cấp.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> MergeConfirmed(string? screenScopeJson, IReadOnlyList<ScreenScopeRow> submitted)
    {
        var stored = Parse(screenScopeJson);
        if (stored.Count == 0)
            return submitted.ToList();

        var result = submitted.ToList();
        var shown = new HashSet<string>(result.Select(r => Normalize(r.Screen)), StringComparer.Ordinal);

        foreach (var row in result)
        {
            var previous = stored.FirstOrDefault(r => Normalize(r.Screen) == Normalize(row.Screen));
            if (previous == null)
                continue;

            var names = new HashSet<string>(row.Functions.Select(f => Normalize(f.Name)), StringComparer.Ordinal);
            foreach (var function in previous.Functions)
            {
                if (names.Add(Normalize(function.Name)) && row.Functions.Count < MaxFunctionsPerScreen)
                    row.Functions.Add(function);
            }
        }

        result.AddRange(stored.Where(r => !shown.Contains(Normalize(r.Screen))));
        return result;
    }

    private static List<ScreenScopeRow> BuildCore(
        IEnumerable<ScreenScopeRow>? stored,
        IEnumerable<ScreenScopeRow>? proposed,
        IReadOnlyList<string> allowedScreens,
        bool respectIncluded,
        bool acceptUserAdded)
    {
        var screens = CleanScreens(allowedScreens);
        // Danh sách cho phép rỗng ⇒ không có gì để hỏi. Trừ khi payload chở dòng người dùng TỰ THÊM: lúc đó
        // bảng vẫn có nội dung thật, và trả rỗng ở đây là nuốt đúng phần họ vừa gõ.
        if (screens.Count == 0 && !acceptUserAdded)
            return new List<ScreenScopeRow>();

        var byScreen = new Dictionary<string, ScreenScopeRow>(StringComparer.Ordinal);
        var fromStore = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<ScreenScopeRow>();
        var addedKeys = new HashSet<string>(StringComparer.Ordinal);

        var sources = (stored ?? Enumerable.Empty<ScreenScopeRow>()).Select(r => (Row: r, Trusted: true))
            .Concat((proposed ?? Enumerable.Empty<ScreenScopeRow>()).Select(r => (Row: r, Trusted: false)));

        foreach (var (row, trusted) in sources)
        {
            if (row == null)
                continue;

            var screen = MatchScreen(row.Screen, screens);
            if (screen == null)
            {
                // MÀN HÌNH NGƯỜI DÙNG TỰ THÊM. Chốt chặn "màn hình bịa" dựng để chặn MODEL, nên nó phải
                // nhường ở đúng chỗ này: người dùng là người có thẩm quyền về phạm vi, và một dòng họ vừa
                // tự gõ rồi bấm gửi mà biến mất trong im lặng là đúng loại quyết định thay họ mà cả bảng
                // sinh ra để chặn. Mọi giới hạn khác vẫn áp: tên bị cắt theo trần, trùng thì bỏ, và cả bảng
                // vẫn không vượt MaxRows.
                if (!acceptUserAdded || !row.AddedByUser)
                    continue;

                var name = Clip((row.Screen ?? string.Empty).Trim(), MaxTextChars);
                if (name.Length == 0 || !addedKeys.Add(Normalize(name)))
                    continue;

                added.Add(NewRow(row, name, respectIncluded, addedByUser: true, trusted));
                continue;
            }

            if (byScreen.TryGetValue(screen, out var existing))
            {
                // Dòng ĐANG LƯU đứng trước nên nó luôn thắng. Đề xuất của model chỉ được lấp vào một dòng
                // CHƯA AI RÀ — xem ghi chú của Build.
                if (!trusted && fromStore.Contains(screen) && !existing.ConfirmedByUser)
                    Enrich(existing, row);
                continue;
            }

            // chữ của DANH SÁCH CHO PHÉP, không phải chữ của model
            byScreen[screen] = NewRow(row, screen, respectIncluded, addedByUser: false, trusted);
            if (trusted)
                fromStore.Add(screen);
        }

        var covered = CoveredScopeItems(byScreen.Values.Concat(added), screens);

        var result = new List<ScreenScopeRow>();
        foreach (var screen in screens)
        {
            if (byScreen.TryGetValue(screen, out var found))
                result.Add(found);
            else if (covered.Contains(Normalize(screen)))
                continue; // mục này đã nằm trong cột chức năng của một màn hình khác — xem CoveredScopeItems
            else
                // Màn hình model bỏ quên: vẫn phải có mặt, TÍCH SẴN. Đưa vào ở trạng thái bỏ tích là ra
                // quyết định loại thay người dùng, còn bỏ hẳn là làm nó biến mất khỏi mọi tầng sau.
                result.Add(new ScreenScopeRow { Screen = screen, Included = true });

            if (result.Count >= MaxRows)
                break;
        }

        // Dòng tự thêm xếp SAU CÙNG — đúng chỗ chúng đứng trên bảng, và cũng là thứ tự đọc dễ nhất khi
        // người dùng đối chiếu tin nhắn kể lại với cái họ vừa gõ.
        foreach (var row in added)
        {
            if (result.Count >= MaxRows)
                break;
            result.Add(row);
        }

        return result;
    }

    /// <summary>
    /// Lấp đề xuất của model vào một dòng CHƯA AI RÀ: chỉ THÊM, không bao giờ ghi đè. Câu "việc của màn"
    /// chỉ được điền khi đang trống, chức năng và lời khai gộp chỉ được thêm mục chưa có.
    /// </summary>
    private static void Enrich(ScreenScopeRow existing, ScreenScopeRow incoming)
    {
        if (existing.Purpose.Length == 0)
            existing.Purpose = Clip((incoming.Purpose ?? string.Empty).Trim(), MaxTextChars);

        foreach (var function in CleanFunctions(incoming.Functions, respectIncluded: false, trusted: false))
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
    /// Một dòng đã chuẩn hoá, dùng chung cho dòng khớp danh sách cho phép và dòng người dùng tự thêm.
    /// <paramref name="trusted"/> = dòng đến từ BẢNG ĐANG LƯU: chỉ nó mới được chở
    /// <see cref="ScreenScopeRow.ConfirmedByUser"/> qua, dòng model đề xuất luôn ra <c>false</c>.
    /// </summary>
    private static ScreenScopeRow NewRow(ScreenScopeRow source, string screen, bool respectIncluded, bool addedByUser, bool trusted)
    {
        return new ScreenScopeRow
        {
            Screen = screen,
            Purpose = Clip((source.Purpose ?? string.Empty).Trim(), MaxTextChars),
            Functions = CleanFunctions(source.Functions, respectIncluded, trusted),
            Covers = CleanCovers(source.Covers, screen),
            Included = !respectIncluded || source.Included,
            AddedByUser = addedByUser,
            ConfirmedByUser = trusted && source.ConfirmedByUser
        };
    }

    /// <summary>
    /// Các mục phạm vi đã được một dòng khai là GỘP vào mình, chuẩn hoá sẵn để đối chiếu.
    ///
    /// <para>
    /// Một mục chỉ được coi là đã gộp khi KHÔNG dòng nào đứng tên nó: dòng luôn thắng lời khai gộp. Không có
    /// luật đó thì model chỉ cần khai bừa tên một màn hình thật vào <see cref="ScreenScopeRow.Covers"/> của
    /// dòng khác là màn hình ấy biến mất khỏi bảng trong im lặng — mà cả tính năng này tồn tại để không thứ
    /// gì rời khỏi phạm vi mà người dùng không nhìn thấy.
    /// </para>
    /// </summary>
    private static HashSet<string> CoveredScopeItems(IEnumerable<ScreenScopeRow> rows, IReadOnlyList<string> screens)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var owned = new HashSet<string>(rows.Select(r => Normalize(r.Screen)), StringComparer.Ordinal);

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
    /// Dự án này đã có ít nhất một dòng người dùng CHỐT chưa. Cột khác null KHÔNG còn đồng nghĩa "đã chốt":
    /// bảng nay là nguồn phạm vi duy nhất nên lượt chắt lọc ghép mục mới vào đó từ trước lúc người dùng
    /// nhìn thấy bảng lần đầu — hỏi cột null là kết luận "đã trả lời rồi" cho một bảng chưa ai rà.
    /// </summary>
    public static bool IsConfirmed(string? json) => Parse(json).Any(r => r.ConfirmedByUser);

    /// <summary>
    /// Bảng còn mục nào CHỜ DUYỆT không — điều kiện mở của <see cref="ScreenScopeGate"/>, và là thứ thay cho
    /// cả phép so tập hợp giữa hai danh sách phạm vi ở bản cũ.
    /// </summary>
    public static bool HasPending(string? json)
    {
        var rows = Parse(json);
        return rows.Any(r => r.Included && !r.ConfirmedByUser)
            || rows.Any(r => r.Included && r.Functions.Any(f => f.Included && !f.ConfirmedByUser));
    }

    /// <summary>
    /// Các MÀN HÌNH đang chờ duyệt: dòng còn tích mà chưa ai rà. Dùng để gọi tên phần mới trong câu dẫn của
    /// lượt bày LẠI (<c>BAChatService.ScreenScopeReshowIntro</c>).
    /// </summary>
    public static List<string> PendingScreens(string? json)
        => Parse(json).Where(r => r.Included && !r.ConfirmedByUser).Select(r => r.Screen.Trim()).ToList();

    /// <summary>
    /// Các CHỨC NĂNG đang chờ duyệt trên những màn hình người dùng ĐÃ chốt, kể kèm tên màn (cùng khuôn với
    /// bản kể chức năng bị bỏ tích ở <see cref="RenderUserMessage"/>).
    ///
    /// <para>
    /// Chỉ tính trên dòng đã chốt để không kể hai lần: chức năng của một màn hình còn chờ duyệt đã nằm
    /// trong <see cref="PendingScreens"/> rồi — cả cái màn hình ấy là mới.
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
    /// Các dòng bảng màn hình CÒN ĐANG CHỜ người dùng gửi — thứ mà view dựng lại sau F5.
    /// <paramref name="confirmedJson"/> là <see cref="ICOGenerator.Domain.Project.ScreenScopeMap"/>,
    /// <paramref name="renderedJson"/> là <c>ScreenScopeMap</c> của lượt BA bày bảng gần nhất.
    ///
    /// <para>
    /// <b>Vì sao không thể hỏi mỗi "dự án đã chốt bảng chưa".</b> Ba bảng kia treo theo DỰ ÁN được vì chúng
    /// chốt đúng một lần: cột trên <c>Project</c> khác null ⇔ bảng đã trả lời xong. Bảng màn hình là cổng
    /// DUY NHẤT mở lại được (<see cref="ScreenScopeGate"/>), nên ở lượt bày LẠI cột đó đã mang dấu chốt từ
    /// lần trước — hỏi nó là kết luận "đã trả lời rồi" cho một bảng người dùng còn chưa kịp nhìn. Bảng hiện
    /// ra ở lượt bày lại, F5 một cái là mất, và không có đường nào khác để gửi: các màn hình mới lại rơi vào
    /// bảng phân quyền ở dạng TRẮNG — đúng lỗ hổng mà đường mở lại sinh ra để bịt.
    /// </para>
    ///
    /// <para>
    /// Phép so vì vậy là từng MỤC của bảng vừa bày với dấu chốt trong bảng đang lưu: còn một màn hình hoặc
    /// một chức năng chưa mang dấu thì bảng ấy vẫn đang chờ. Vòng lặp có đáy vì đường GỬI đóng dấu cho MỌI
    /// dòng của bảng vừa bày (<see cref="Sanitize"/>), kể cả dòng bỏ tích — gửi xong là panel tự đóng.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> PendingRows(string? confirmedJson, string? renderedJson)
    {
        var rendered = Parse(renderedJson);
        if (rendered.Count == 0)
            return rendered;

        var stored = Parse(confirmedJson);
        if (stored.Count == 0)
            return rendered;

        foreach (var row in rendered)
        {
            var match = stored.FirstOrDefault(r => Normalize(r.Screen) == Normalize(row.Screen));
            // Dòng bảng vừa bày mà bảng đang lưu không đứng tên hoặc chưa đóng dấu ⇒ còn chờ. Mục đã được
            // khai GỘP vào một màn hình khác không tính: nó không bao giờ trở thành một dòng riêng.
            if (match == null)
            {
                if (stored.Any(r => r.Covers.Any(c => Normalize(c) == Normalize(row.Screen))))
                    continue;
                return rendered;
            }

            if (!match.ConfirmedByUser)
                return rendered;

            foreach (var function in row.Functions)
            {
                var known = match.Functions.FirstOrDefault(f => Normalize(f.Name) == Normalize(function.Name));
                if (known == null || !known.ConfirmedByUser)
                    return rendered;
            }
        }

        return new List<ScreenScopeRow>();
    }

    /// <summary>
    /// PHẠM VI MÀN HÌNH THẬT SỰ của dự án — nguồn dòng cho bảng phân quyền, cho danh sách cho phép của lượt
    /// bày bảng, và cho mục <c>## 6. Screens To Generate</c> của spec.
    ///
    /// <para>
    /// Là mọi dòng CÒN TÍCH, chốt rồi hay chưa. Mục chưa chốt phải có mặt vì buổi phỏng vấn còn tiếp tục
    /// sau lúc bảng được chốt, và một màn hình lộ ra ở lượt sau mà không vào được bảng phân quyền thì mặc
    /// nhiên "không ai được xem"; mục người dùng đã BỎ TÍCH thì không bao giờ quay lại — dòng bia ở lại
    /// trong bảng chính là thứ bảo đảm điều đó (xem <see cref="Merge"/>).
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
    /// phải là thứ mất đi cũng không xoá được quyết định của ai. Mục mới vào ở trạng thái CHỜ DUYỆT, và chỗ
    /// nó được quyết là bảng bày ra ở lượt sau.
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
                // BIA. Người dùng đã loại màn hình này; dựng lại nó là mở lại thứ họ vừa đóng.
                continue;
            }

            foreach (var raw in addition.Functions ?? new List<string>())
            {
                var function = Clip((raw ?? string.Empty).Trim(), MaxTextChars);
                if (function.Length == 0 || row.Functions.Count >= MaxFunctionsPerScreen)
                    continue;
                // So khớp tên chức năng bằng CHỨA-NHAU sau chuẩn hoá, cùng phép so với tên màn hình: model
                // chép lại một chức năng đã có bằng chữ của nó là chuyện thường, và mỗi lần như thế là một
                // mục chờ duyệt giả — tức một lượt bày bảng mà người dùng không có việc gì để làm.
                if (MatchScreen(function, row.Functions.Select(f => f.Name).ToList()) != null)
                    continue;

                row.Functions.Add(new ScreenFunction { Name = function, Included = true });
                changed = true;
            }
        }

        return changed ? rows : null;
    }

    /// <summary>
    /// Các dòng ĐANG LƯU để gieo cho lượt bày bảng: chỉ màn hình CÒN TÍCH, và trong mỗi màn chỉ chức năng
    /// CÒN TÍCH — chốt rồi hay còn chờ duyệt đều vào, vì cả hai đều đang là phạm vi của ứng dụng.
    ///
    /// <para>
    /// Không có hạt giống này thì lần bày lại là một lượt phá hoại: <see cref="Build"/> dựng bảng từ đề
    /// xuất TƯƠI của model, nên mọi thứ người dùng đã tự tay rà ở lần chốt trước — việc của từng màn, danh
    /// sách chức năng, ô "phục vụ bước nào" — bị thay bằng bản model vừa đoán lại, và họ phải rà lần thứ
    /// hai từ số không cho những màn hình chẳng liên quan gì tới thứ vừa lộ ra. Lọc theo cờ tích là phần
    /// còn lại của cùng một luật: <see cref="Build"/> cố ý trả mọi dòng ở trạng thái TÍCH SẴN, nên đưa cả
    /// dòng/chức năng đã bỏ tích vào hạt giống là bật lại đúng thứ họ vừa tắt. Phần bị lọc ra không mất:
    /// <see cref="MergeConfirmed"/> giữ nó lại lúc lưu.
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
    {
        var actions = FlowMapBuilder.IncludedActions(flowMapJson);
        if (actions.Count == 0)
            return new List<string>();

        var covered = rows
            .Where(r => r.Included)
            .SelectMany(r => r.Functions)
            .Where(f => f.Included)
            .SelectMany(f => f.FlowSteps)
            .Select(Normalize)
            .Where(s => s.Length > 0)
            .ToList();

        return actions
            .Where(action =>
            {
                var key = Normalize(action);
                return key.Length > 0
                       && !covered.Any(c => c.Contains(key, StringComparison.Ordinal) || key.Contains(c, StringComparison.Ordinal));
            })
            .ToList();
    }

    /// <summary>
    /// XẾP CHỖ cho các bước mồ côi: nhận lời xếp chỗ của lượt <c>ScreenStepPlacementService</c> và ghi
    /// chúng vào bảng, để bảng hiện ra đã KÍN thay vì hiện ra kèm một câu hỏi ngược người dùng.
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
    ///   trở thành một đường vòng cho model viết lại cả bảng — kể cả phần người dùng đã tự tay rà ở lần
    ///   chốt trước (bảng này bày LẠI được, xem <see cref="SeedRows"/>).</item>
    ///   <item><b>Chỉ THÊM, không bao giờ bớt.</b> Không dòng nào bị xóa, không cờ tích nào bị đổi, không
    ///   câu "việc của màn" nào đang có bị viết đè. Bước về một chức năng đã có ⇒ gắn thêm vào ô "phục vụ
    ///   bước" của nó; không có chức năng nào tên vậy ⇒ một chức năng MỚI ở cuối màn.</item>
    ///   <item><b>Màn hình MỚI được nhận — ngoại lệ THỨ HAI của chốt chặn "màn hình bịa"</b> (thứ nhất là
    ///   dòng người dùng tự thêm), và nó hẹp
    ///   đúng bằng lý do sinh ra nó: dòng mới phải mang một bước mà NGƯỜI DÙNG đã chốt ở bảng luồng và
    ///   không màn hình nào đang có phụ trách. Chốt chặn kia dựng để chặn model rải thêm màn hình cho đủ
    ///   bộ; ở đây thì thứ chặn nó là phép kiểm tất định vừa chạy, không phải một danh sách cho phép. Dòng
    ///   ra TÍCH SẴN như mọi dòng khác và người dùng bỏ tích được — còn cửa duy nhất trước đây, "nhắn cho
    ///   mình biết nếu thiếu hẳn một màn hình", đòi họ nhận ra điều đó trước.</item>
    /// </list>
    ///
    /// <para>
    /// Cờ <see cref="ScreenScopeRow.AddedByUser"/> của dòng mới vẫn là <c>false</c>: đây là đề xuất của BA,
    /// và mượn cờ ấy là gán chữ ký người dùng lên một dòng họ chưa nhìn thấy — đúng thứ cờ đó được dựng ra
    /// để phân biệt.
    /// </para>
    ///
    /// <para>
    /// Danh sách trả về là danh sách MỚI, nhưng các dòng bên trong là chính các object của
    /// <paramref name="rows"/> đã được ghi thêm chức năng/bước — giữ lại tham chiếu bảng cũ không cho bạn
    /// một ảnh chụp "trước khi xếp". Chỗ cần so trước/sau thì chụp riêng thứ mình cần (xem
    /// <c>BAChatService.ScreenScopePlacementNotice</c> chỉ chụp danh sách TÊN màn hình).
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
    /// Khối ngữ cảnh gắn vào MỌI lượt chat sau khi bảng đã chốt, vào lượt distill bản đồ bao phủ, và vào
    /// prompt sinh AI Design Spec. Trả null khi chưa chốt.
    ///
    /// <para>
    /// Danh sách chức năng ở đây là thứ bảng phân quyền phải đứng lên: dòng của bảng đó là một cặp
    /// (màn hình × chức năng), và trước khi có khối này thì vế chức năng do model tự nghĩ ra ngay tại lượt
    /// bày bảng — tức người dùng tích quyền cho những việc chưa ai duyệt là có thật.
    /// </para>
    /// </summary>
    public static string? RenderConfirmedBlock(string? json)
    {
        // CHỈ phần mang dấu chốt. Bảng nay chở cả mục chưa ai rà, mà khối này mở đầu bằng "người dùng đã
        // CHỐT" và đóng lại bằng lệnh cấm hỏi lại việc của từng màn — đưa một dòng chờ duyệt vào đây là
        // đóng dấu chữ ký người dùng lên nó rồi cấm BA hỏi về nó, tức bịt đúng lượt sinh ra để hỏi.
        var rows = Parse(json).Where(r => r.ConfirmedByUser).ToList();
        if (rows.Count == 0)
            return null;

        var kept = rows.Where(r => r.Included).ToList();
        var dropped = rows.Where(r => !r.Included).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng màn hình đã được NGƯỜI DÙNG CHỐT (phạm vi màn hình của ứng dụng) ---");
        sb.AppendLine("Đây là TOÀN BỘ màn hình của ứng dụng và các chức năng trên từng màn. KHÔNG thêm màn "
            + "hình mới ngoài danh sách này, KHÔNG thêm chức năng ngoài các chức năng dưới đây, và KHÔNG hỏi "
            + "lại việc của từng màn.");

        // Cùng luật với khối của bảng đối tượng: câu "việc của màn" là văn xuôi BA đặt, không phải quyết
        // định người dùng đã rà, nên nó đứng riêng có gắn xuất xứ thay vì nối vào dòng tên màn hình.
        if (kept.Any(r => !string.IsNullOrWhiteSpace(r.Purpose)))
            sb.AppendLine("Dòng \"việc của màn\" là câu CHÍNH BẠN đặt lúc bày bảng, KHÔNG phải lời người "
                + "dùng: đừng trích nó làm bằng chứng và đừng lấy nó làm một vế mâu thuẫn với điều họ nói.");

        foreach (var row in kept)
        {
            sb.AppendLine($"* {row.Screen}");

            if (!string.IsNullOrWhiteSpace(row.Purpose))
                sb.AppendLine($"  - việc của màn (BA tự đặt, chưa ai rà): {row.Purpose}");

            foreach (var function in row.Functions.Where(f => f.Included && f.ConfirmedByUser))
            {
                var steps = function.FlowSteps.Count > 0
                    ? $" (phục vụ bước: {string.Join("; ", function.FlowSteps)})"
                    : string.Empty;
                sb.AppendLine($"  - chức năng: {function.Name}{steps}");
            }

            var droppedFunctions = row.Functions.Where(f => !f.Included && f.ConfirmedByUser).Select(f => f.Name).ToList();
            if (droppedFunctions.Count > 0)
                sb.AppendLine($"  - chức năng người dùng đã LOẠI (đừng dựng, đừng nhắc lại): {string.Join(", ", droppedFunctions)}");

            if (row.Covers.Count > 0)
                sb.AppendLine($"  - đã gộp vào màn này: {string.Join(", ", row.Covers)}");
        }

        if (dropped.Count > 0)
            sb.AppendLine("Màn hình người dùng đã LOẠI (đừng dựng, đừng nhắc lại): "
                + string.Join(", ", dropped.Select(r => r.Screen)) + ".");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Tin nhắn mà TRÌNH DUYỆT gửi tiếp vào khung chat sau khi bảng đã lưu — cùng khuôn hai bước với bảng
    /// cột và bảng phân quyền, và soạn ở server vì cùng lý do: bản kể phải khớp đúng bản đã lưu.
    /// </summary>
    public static string RenderUserMessage(IReadOnlyList<ScreenScopeRow> rows)
    {
        if (rows.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Mình đã rà bảng màn hình, đây là các màn hình ứng dụng cần có:");
        sb.AppendLine();

        foreach (var row in rows.Where(r => r.Included))
        {
            var functions = row.Functions.Where(f => f.Included).Select(f => f.Name).ToList();
            sb.AppendLine($"- {row.Screen}"
                + (functions.Count > 0 ? $" [chức năng: {string.Join(", ", functions)}]" : string.Empty));

            if (row.Covers.Count > 0)
                sb.AppendLine($"  (mình gộp vào màn này: {string.Join(", ", row.Covers)})");
        }

        // Màn hình TỰ THÊM cũng phải được gọi tên, cùng lý do với các dòng bị loại: đây là chỗ bảng khác đi
        // so với thứ BA vừa bày ra, và mọi tầng chắt lọc phía sau đọc bản kể này chứ không đọc cột DB. Nói
        // rõ nguồn gốc còn giữ cho BA khỏi hỏi lại "màn này ở đâu ra" ở lượt kế.
        var addedByUser = rows.Where(r => r.Included && r.AddedByUser).ToList();
        if (addedByUser.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Các màn hình mình tự bổ sung vào bảng: "
                + string.Join(", ", addedByUser.Select(r => r.Screen)) + ".");
        }

        // Màn hình và chức năng bị loại phải được NÓI RA — cùng lý do bảng cột gọi tên cả cột bị bỏ tích:
        // im lặng thì người dùng không có bằng chứng nào cho thấy mình vừa loại đúng thứ định loại.
        var dropped = rows.Where(r => !r.Included).ToList();
        if (dropped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Các màn hình mình KHÔNG cần: " + string.Join(", ", dropped.Select(r => r.Screen)) + ".");
        }

        var droppedFunctions = rows
            .Where(r => r.Included)
            .SelectMany(r => r.Functions.Where(f => !f.Included).Select(f => $"{f.Name} (ở {r.Screen})"))
            .ToList();
        if (droppedFunctions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Các chức năng mình KHÔNG cần: " + string.Join(", ", droppedFunctions) + ".");
        }

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

    // Chức năng KHÔNG có tên thì không có gì để tích: bỏ. Đây cũng là đường mà dòng người dùng thêm bằng nút
    // "+ thêm chức năng" rồi bỏ trống đi ra — bấm thêm mà không gõ gì thì nó không thành một chức năng.
    private static List<ScreenFunction> CleanFunctions(IEnumerable<ScreenFunction>? proposed, bool respectIncluded, bool trusted)
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
                Included = !respectIncluded || function.Included,
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
    // sẽ đẻ ra mục chờ duyệt giả mỗi lượt chat.
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

    private static string Normalize(string value)
        => string.Join(' ', (value ?? string.Empty).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim(' ', '.', ',', ':', ';', '-', '–');

    private static string Clip(string value, int max)
        => value.Length > max ? value[..max] : value;
}
