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
///   hai danh sách khác nhau, và trộn chúng là lỗi câm, xem <c>ConfirmScreenScopeUseCase</c>. Ngoại lệ DUY
///   NHẤT: dòng người dùng TỰ THÊM bằng nút "thêm màn hình" — chốt chặn này dựng để chặn model, không phải
///   để chặn người dùng, xem <see cref="ScreenScopeRow.AddedByUser"/>.</item>
///   <item><b>Màn hình bị bỏ quên.</b> Mục phạm vi model không nhắc tới vẫn được BỔ SUNG vào cuối bảng —
///   ở trạng thái TÍCH SẴN như mọi dòng khác, vì "BA quên nêu" không phải "người dùng đã loại". Bỏ nó đi
///   là ra một quyết định thay người dùng ở đúng chỗ họ không nhìn thấy để phản đối. Ngoại lệ DUY NHẤT:
///   mục đã được một dòng khai là gộp vào mình qua <see cref="ScreenScopeRow.Covers"/> — xem
///   <see cref="CoveredScopeItems"/>.</item>
///   <item><b>Bước luồng không chức năng nào phụ trách.</b> Chốt chặn RIÊNG của bảng này và là lý do
///   <see cref="ScreenFunction.FlowSteps"/> tồn tại — xem <see cref="UncoveredActions"/>.</item>
/// </list>
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
    /// Bảng cuối cùng cho lượt BA BÀY BẢNG: giữ các dòng khớp phạm vi đã chắt, bỏ dòng bịa/trùng, bổ sung
    /// mọi màn hình chưa được nhắc tới (trừ mục đã được gộp), và luôn xếp theo thứ tự của
    /// <paramref name="plannedScope"/>. Trả rỗng khi phạm vi trống — không có phạm vi thì bảng không có gì
    /// để hỏi.
    ///
    /// <para>
    /// Khác <see cref="Sanitize"/> ở đúng chỗ cờ <c>included</c>: ở đây mọi dòng và mọi chức năng ra TÍCH
    /// SẴN bất kể model trả gì, vì cờ đó là chỗ NGƯỜI DÙNG loại một màn hình, không phải chỗ model tự phủ
    /// nhận đề xuất của mình. Structured output buộc điền đủ trường, nên một model điền <c>false</c> cho có
    /// sẽ bỏ tích sạch bảng và người dùng gửi đi một phạm vi RỖNG trong khi tưởng mình vừa xác nhận cả
    /// ứng dụng.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> Build(IEnumerable<ScreenScopeRow>? proposed, IReadOnlyList<string> plannedScope)
        => BuildCore(proposed, plannedScope, respectIncluded: false, acceptUserAdded: false);

    /// <summary>
    /// Bản chuẩn hoá cho dữ liệu ĐẾN TỪ TRÌNH DUYỆT. Server không tin bảng client gửi kể cả khi chính nó
    /// vừa render ra: tên màn hình vẫn phải khớp lại <paramref name="allowedScreens"/>. Khác
    /// <see cref="Build"/>: giữ đúng lựa chọn tích/bỏ tích của người dùng ở CẢ hai cấp (màn hình và chức
    /// năng), và NHẬN các dòng người dùng TỰ
    /// THÊM dù chúng không có trong <paramref name="allowedScreens"/> — xem
    /// <see cref="ScreenScopeRow.AddedByUser"/>. Dòng tự thêm xếp SAU CÙNG, đúng chỗ chúng đứng trên bảng.
    ///
    /// <para>
    /// <paramref name="allowedScreens"/> phải là danh sách màn hình của CHÍNH BẢNG SERVER ĐÃ RENDER, không
    /// phải <c>Project.PlannedScope</c> đọc lại lúc gửi. Hai thứ đó KHÔNG bằng nhau: lượt chắt lọc
    /// <c>PlannedScope</c> chạy ở hậu kỳ ngay lượt bày bảng (xem <c>InterviewOutlookService</c>), nên tới
    /// lúc người dùng bấm gửi thì danh sách đã bị viết lại — chỉ cần model diễn đạt khác đi một chữ là mọi
    /// dòng trượt khỏi <see cref="MatchScreen"/>, cả bảng người dùng vừa rà bị bỏ, và chỗ của nó là các mục
    /// phạm vi mới bù vào ở dạng TRẮNG. Người dùng thấy mình gửi một bảng đã điền và nhận lại một danh sách
    /// tên suông. Xem <c>ConfirmScreenScopeUseCase</c>.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> Sanitize(IEnumerable<ScreenScopeRow>? submitted, IReadOnlyList<string> allowedScreens)
    {
        // Cờ TỰ THÊM là thứ DUY NHẤT của dòng còn sống qua đường gửi ngoài phần người dùng tự điền: nó là
        // một sự thật về nguồn gốc của dòng mà RenderUserMessage còn phải kể lại.
        return BuildCore(submitted, allowedScreens, respectIncluded: true, acceptUserAdded: true);
    }

    private static List<ScreenScopeRow> BuildCore(
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
        var added = new List<ScreenScopeRow>();
        var addedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in proposed ?? Enumerable.Empty<ScreenScopeRow>())
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

                added.Add(NewRow(row, name, respectIncluded, addedByUser: true));
                continue;
            }

            if (byScreen.ContainsKey(screen))
                continue;

            // chữ của DANH SÁCH CHO PHÉP, không phải chữ của model
            byScreen[screen] = NewRow(row, screen, respectIncluded, addedByUser: false);
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

    /// <summary>Một dòng đã chuẩn hoá, dùng chung cho dòng khớp danh sách cho phép và dòng người dùng tự thêm.</summary>
    private static ScreenScopeRow NewRow(ScreenScopeRow source, string screen, bool respectIncluded, bool addedByUser)
    {
        return new ScreenScopeRow
        {
            Screen = screen,
            Purpose = Clip((source.Purpose ?? string.Empty).Trim(), MaxTextChars),
            Functions = CleanFunctions(source.Functions, respectIncluded),
            Covers = CleanCovers(source.Covers, screen),
            Included = !respectIncluded || source.Included,
            AddedByUser = addedByUser
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

    /// <summary>Dự án này đã chốt bảng màn hình chưa.</summary>
    public static bool IsConfirmed(string? json) => Parse(json).Count > 0;

    /// <summary>
    /// Các dòng bảng màn hình CÒN ĐANG CHỜ người dùng gửi — thứ mà view dựng lại sau F5.
    /// <paramref name="confirmedJson"/> là <see cref="ICOGenerator.Domain.Project.ScreenScopeMap"/>,
    /// <paramref name="renderedJson"/> là <c>ScreenScopeMap</c> của lượt BA bày bảng gần nhất (cùng lượt mà
    /// <c>ConfirmScreenScopeUseCase</c> lấy danh sách đối chiếu).
    ///
    /// <para>
    /// <b>Vì sao không thể hỏi mỗi "dự án đã chốt bảng chưa".</b> Ba bảng kia treo theo DỰ ÁN được vì chúng
    /// chốt đúng một lần: cột trên <c>Project</c> khác null ⇔ bảng đã trả lời xong. Bảng màn hình là cổng
    /// DUY NHẤT mở lại được (<see cref="ScreenScopeGate"/>), nên ở lượt bày LẠI cột đó đã khác null từ lần
    /// chốt trước — hỏi nó là kết luận "đã trả lời rồi" cho một bảng người dùng còn chưa kịp nhìn. Bảng
    /// hiện ra ở lượt bày lại, F5 một cái là mất, và không có đường nào khác để gửi: các màn hình mới lại
    /// rơi vào bảng phân quyền ở dạng TRẮNG — đúng lỗ hổng mà đường mở lại sinh ra để bịt.
    /// </para>
    ///
    /// <para>
    /// Nên phép so là bảng ĐÃ CHỐT với chính bảng SERVER VỪA BÀY, không phải với
    /// <c>Project.PlannedScope</c>: <c>PlannedScope</c> bị lượt chắt lọc "triển vọng phỏng vấn" ghi đè ngay
    /// ở hậu kỳ lượt bày bảng, nên treo panel vào nó là để một lời gọi LLM chạy sau lưng quyết định bảng
    /// còn hay mất — cùng lý do <c>ConfirmScreenScopeUseCase</c> lấy danh sách đối chiếu từ lượt hội thoại.
    /// Vòng lặp vẫn có đáy: gửi xong thì mọi màn hình của bảng vừa bày đều có mặt trong bản chốt (kể cả
    /// dòng bỏ tích và mục khai gộp — xem <see cref="NewScreens(string?, IReadOnlyList{string})"/>), nên
    /// panel tự đóng.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> PendingRows(string? confirmedJson, string? renderedJson)
    {
        var rendered = Parse(renderedJson);
        if (rendered.Count == 0 || !IsConfirmed(confirmedJson))
            return rendered;

        // Bảng vừa bày chỉ còn chờ khi nó mang màn hình mà bản đã chốt chưa biết — tức đúng lượt bày LẠI.
        return NewScreens(confirmedJson, rendered.Select(r => r.Screen).ToList()).Count > 0
            ? rendered
            : new List<ScreenScopeRow>();
    }

    /// <summary>
    /// PHẠM VI MÀN HÌNH THẬT SỰ của dự án — nguồn dòng cho bảng phân quyền và cho mục
    /// <c>## 6. Screens To Generate</c> của spec.
    ///
    /// <para>
    /// Chưa chốt bảng ⇒ trả nguyên <paramref name="plannedScope"/>, tức mọi thứ chạy đúng như trước khi có
    /// tính năng này. Đã chốt ⇒ các dòng người dùng GIỮ, cộng những mục phạm vi mới lộ ra SAU lúc chốt.
    /// Mục mới phải được thêm vào (buổi phỏng vấn còn tiếp tục sau khi bảng đã chốt, và một màn hình lộ ra
    /// ở lượt sau mà không vào được bảng phân quyền thì mặc nhiên "không ai được xem"); còn mục người dùng
    /// đã BỎ TÍCH — hoặc đã được GỘP vào một màn hình khác — thì không bao giờ quay lại, kể cả khi nó vẫn
    /// nằm trong PlannedScope. Mở lại thứ họ vừa đóng là đúng lỗi mà bảng cột đã cấm.
    /// <c>ConfirmScreenScopeUseCase</c> ghi ngược phạm vi đã duyệt lên PlannedScope nên lượt chắt lọc kế
    /// tiếp không còn mang mục đã đóng theo nữa, nhưng phép lọc ở đây vẫn là thứ BẢO ĐẢM điều đó: lượt chắt
    /// lọc là một lời gọi LLM, và một lời gọi LLM không phải một bất biến.
    /// </para>
    /// </summary>
    public static List<string> EffectiveScreens(string? screenScopeJson, IReadOnlyList<string> plannedScope)
    {
        var rows = Parse(screenScopeJson);
        if (rows.Count == 0)
            return CleanScreens(plannedScope);

        var kept = rows.Where(r => r.Included).ToList();
        // KHÔNG dòng nào được giữ ⇒ coi là bảng hỏng, KHÔNG phải "ứng dụng không có màn hình nào". Trả
        // rỗng ở đây là khóa chết cả tuyến trong im lặng: cổng bảng phân quyền đòi phạm vi có mục mới mở,
        // mà dòng phân quyền chỉ [RÕ] sau khi bảng đó chốt ⇒ nút "Write Requirement" không bao giờ sáng và
        // không có gì trên màn hình nói vì sao. Cùng luật fail-open với bảng cột không khớp hàng tiêu đề
        // nào: để lọt vài mục thừa rẻ hơn nhiều so với cắt sạch.
        if (kept.Count == 0)
            return CleanScreens(plannedScope);

        var result = kept.Select(r => r.Screen.Trim()).ToList();
        result.AddRange(NewScreens(rows, plannedScope));
        return result;
    }

    /// <summary>
    /// Các màn hình LỘ RA SAU lúc bảng được chốt: mục của <paramref name="plannedScope"/> mà bảng đã chốt
    /// không đứng tên và cũng không khai là đã gộp vào một dòng nào.
    ///
    /// <para>
    /// <b>Vì sao nó phải là một hàm công khai chứ chỉ nằm trong <see cref="EffectiveScreens"/>.</b> Buổi
    /// phỏng vấn còn tiếp tục sau khi bảng đã chốt, và phạm vi vẫn trôi: ca thật (dự án Learning and
    /// Development 7) người dùng chốt bảng ở lượt 23 rồi tới lượt 33 mới nói *"sĩ số tối thiểu và tối đa lấy
    /// từ danh sách khóa học được quản lý ở một màn hình riêng"* — một màn hình mới, cùng với hai danh mục
    /// nữa (phòng học, người dạy) mà Admin được chốt là người quản lý. Trước đây
    /// <see cref="EffectiveScreens"/> bù chúng vào bảng phân quyền ở dạng TRẮNG: không việc, không chức
    /// năng, không bước luồng — trong khi khối ngữ cảnh của bảng đã chốt lại CẤM BA hỏi lại việc của từng
    /// màn. Kết quả là ba màn hình đi vào tài liệu và vào bản demo mà không ai biết chúng để làm gì.
    /// <see cref="ScreenScopeGate"/> dùng hàm này để mở lại bảng đúng lúc đó.
    /// </para>
    ///
    /// <para>
    /// Bảng chưa chốt, hoặc chốt mà không dòng nào được giữ (bảng hỏng — xem
    /// <see cref="EffectiveScreens"/>) ⇒ rỗng: lúc đó không có "sau lúc chốt" nào để so, và mở lại một bảng
    /// dựng trên một bản chốt hỏng chỉ làm người dùng rà lại từ đầu.
    /// </para>
    /// </summary>
    public static List<string> NewScreens(string? screenScopeJson, IReadOnlyList<string> plannedScope)
    {
        var rows = Parse(screenScopeJson);
        if (rows.Count == 0 || !rows.Any(r => r.Included))
            return new List<string>();

        return NewScreens(rows, plannedScope);
    }

    private static List<string> NewScreens(IReadOnlyList<ScreenScopeRow> rows, IReadOnlyList<string> plannedScope)
    {
        // Mục đã BỎ TÍCH hoặc đã được GỘP vào một màn hình khác thì không bao giờ quay lại, kể cả khi nó
        // vẫn nằm trong PlannedScope — mở lại thứ người dùng vừa đóng là đúng lỗi mà bảng cột đã cấm. Vì
        // vậy "đã biết" gồm MỌI dòng của bảng (cả dòng bỏ tích) cộng mọi lời khai gộp.
        var known = new HashSet<string>(rows.Select(r => Normalize(r.Screen)), StringComparer.Ordinal);
        foreach (var item in rows.SelectMany(r => r.Covers))
            known.Add(Normalize(item));

        var result = new List<string>();
        foreach (var raw in CleanScreens(plannedScope))
        {
            if (known.Add(Normalize(raw)))
                result.Add(raw);
        }

        return result;
    }

    /// <summary>
    /// Các dòng để BÀY LẠI một bảng đã chốt: chỉ màn hình CÒN TÍCH, và trong mỗi màn chỉ chức năng CÒN
    /// TÍCH. Dùng làm hạt giống cho <see cref="Build"/> khi <see cref="ScreenScopeGate"/> mở lại bảng vì có
    /// màn hình mới (<see cref="NewScreens(string?, IReadOnlyList{string})"/>).
    ///
    /// <para>
    /// Không có hạt giống này thì lần bày lại là một lượt phá hoại: <see cref="Build"/> dựng bảng từ đề
    /// xuất TƯƠI của model, nên mọi thứ người dùng đã tự tay rà ở lần chốt trước — việc của từng màn, danh
    /// sách chức năng, ô "phục vụ bước nào" — bị thay bằng bản model vừa đoán lại, và họ phải rà lần thứ
    /// hai từ số không cho những màn hình chẳng liên quan gì tới thứ vừa lộ ra. Lọc theo cờ tích là phần
    /// còn lại của cùng một luật: <see cref="Build"/> cố ý trả mọi dòng ở trạng thái TÍCH SẴN, nên đưa cả
    /// dòng/chức năng đã bỏ tích vào hạt giống là bật lại đúng thứ họ vừa tắt.
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
        var rows = Parse(json);
        if (rows.Count == 0)
            return null;

        var kept = rows.Where(r => r.Included).ToList();
        var dropped = rows.Where(r => !r.Included).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng màn hình đã được NGƯỜI DÙNG CHỐT (phạm vi màn hình của ứng dụng) ---");
        sb.AppendLine("Đây là TOÀN BỘ màn hình của ứng dụng và các chức năng trên từng màn. KHÔNG thêm màn "
            + "hình mới ngoài danh sách này, KHÔNG thêm chức năng ngoài các chức năng dưới đây, và KHÔNG hỏi "
            + "lại việc của từng màn.");

        foreach (var row in kept)
        {
            var purpose = string.IsNullOrWhiteSpace(row.Purpose) ? string.Empty : $" — {row.Purpose}";
            sb.AppendLine($"* {row.Screen}{purpose}");

            foreach (var function in row.Functions.Where(f => f.Included))
            {
                var steps = function.FlowSteps.Count > 0
                    ? $" (phục vụ bước: {string.Join("; ", function.FlowSteps)})"
                    : string.Empty;
                sb.AppendLine($"  - chức năng: {function.Name}{steps}");
            }

            var droppedFunctions = row.Functions.Where(f => !f.Included).Select(f => f.Name).ToList();
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
            var purpose = string.IsNullOrWhiteSpace(row.Purpose) ? string.Empty : $" — {row.Purpose}";
            var functions = row.Functions.Where(f => f.Included).Select(f => f.Name).ToList();
            sb.AppendLine($"- {row.Screen}{purpose}"
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
    private static List<ScreenFunction> CleanFunctions(IEnumerable<ScreenFunction>? proposed, bool respectIncluded)
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
                Included = !respectIncluded || function.Included
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
