using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng thông báo / nhắc nhở" — bảng CUỐI CÙNG của buổi phỏng vấn: mỗi sự kiện một
/// dòng, người nhận chính (To) và người nhận đồng gửi (CC) chọn từ DANH SÁCH NGƯỜI NHẬN của dự án
/// (<see cref="RecipientOptions"/>). Xem <see cref="NotificationMapRow"/> cho lý do đầy đủ.
///
/// <para>
/// <b>Dòng và cột đều VAY từ hai bảng trước, và đó là lý do bảng này đứng cuối.</b> Dòng gieo từ vòng đời
/// trạng thái của bảng đối tượng đã chốt (<see cref="SeedRows"/>) — người dùng vừa tự tay rà chúng, nên
/// không có dòng nào phải hỏi lại "sự kiện này có thật không". Danh sách người nhận gieo từ các VAI TRÒ của
/// bảng phân quyền đã chốt (<see cref="SeedRecipients"/>): vai trò của ứng dụng ĐANG THIẾT KẾ chỉ tồn tại
/// trong hội thoại, không bảng nào trong DB liệt kê chúng (vai trò của chính ICOGenerator cũng không nằm
/// trong DB — chúng chỉ tồn tại trong claim của phiên đăng nhập — và dù có cũng không liên quan).
/// </para>
///
/// <para>
/// <b>Danh sách người nhận là một BẢNG người dùng tự sửa được, không phải một hằng số của cơ chế.</b> Bản
/// gieo chỉ là điểm xuất phát: bảng "Danh sách người nhận" đứng ngay trên bảng thông báo cho thêm / sửa chữ
/// / xóa từng mục, và bộ mục đó được lưu ở <c>Project.NotificationRecipients</c> rồi trở thành nguồn của
/// MỌI ô To/CC. Trước đó danh sách là tất định và người dùng không có đường nào thêm một người nhận thật
/// không nằm trong bảng phân quyền — lối thoát duy nhất của họ là bỏ tích "Cần", tức ghi vào tài liệu "sự
/// kiện này không cần email" chỉ vì thiếu một tùy chọn.
/// </para>
///
/// <para>
/// Ba chốt chặn tất định, cùng bộ luật với bốn bảng trước:
/// </para>
/// <list type="bullet">
///   <item><b>Sự kiện bịa bị loại.</b> Dòng model đề xuất mà không khớp một chuyển trạng thái nào của bảng
///   đối tượng chỉ đi qua được khi nó có TRÍCH DẪN — đó là đường cho các dòng NHẮC NHỞ thật ("trước hạn 3
///   ngày") mà bảng đối tượng không gieo ra được, không phải cửa cho model nghĩ thêm sự kiện.</item>
///   <item><b>Sự kiện bị bỏ quên vẫn phải có mặt, và ở trạng thái TÍCH SẴN.</b> Model chỉ nêu vài chuyển
///   trạng thái nó nhớ thì các chuyển trạng thái còn lại biến mất khỏi bảng và mặc nhiên thành "không báo
///   cho ai" mà người dùng không bao giờ nhìn thấy để phản đối — đúng khiếm khuyết của hàng chip mà bảng
///   sinh ra để chữa.</item>
///   <item><b>Người nhận phải nằm trong danh sách của dự án.</b> Giá trị không khớp mục nào bị bỏ — kể cả
///   khi MODEL viết ra nó. Người dùng thêm được người nhận mới, model thì không: một người nhận model bịa
///   ("Phòng Nhân sự" ở dự án không có bộ phận đó) đi thẳng vào spec rồi vào POC, và không ai đọc lại bảng
///   mà nhận ra được. Vì cùng lý do, danh sách đối chiếu ở đường GỬI được đọc từ cột của dự án chứ không
///   lấy từ payload — xem <c>ConfirmNotificationMapUseCase</c>.</item>
/// </list>
///
/// <para>
/// <b>BẤT BIẾN của một bảng ĐÃ LƯU: mỗi dòng chỉ có HAI trạng thái</b> — bỏ tích (<c>Needed = false</c>:
/// không gửi email, một quyết định hợp lệ) hoặc còn tích kèm <c>To</c> KHÔNG RỖNG. Trạng thái thứ ba —
/// "cần gửi nhưng chưa chọn ai" — từng được cho phép và trả giá đắt: nó hạ nhóm «Thông báo / nhắc nhở»
/// xuống <c>[MỘT PHẦN]</c>, khóa nút "Write Requirement", rồi bắt BA đi hỏi lại từng sự kiện trong khung
/// chat (mỗi sự kiện hai lượt: To rồi CC) — đúng vòng hỏi lẻ mà cả cái bảng này sinh ra để thay thế. Ca
/// thật: một bảng 8 dòng gửi đi với 7 dòng trống người nhận ⇒ 14 lượt chat ở cuối một buổi 78 lượt.
/// Bất biến được chặn ở <b>đường GỬI</b> (<see cref="MissingRecipients"/>, gọi từ
/// <c>ConfirmNotificationMapUseCase</c> — không hợp lệ thì KHÔNG lưu gì cả), còn trình duyệt chỉ là phanh
/// phụ: người dùng bấm gửi khi còn dòng trống thì popup bắt họ chọn người nhận hoặc bỏ tích.
/// </para>
/// </summary>
public static class NotificationMapBuilder
{
    /// <summary>Trần số dòng. Nhiều hơn thì bảng không rà nổi trong một lượt.</summary>
    public const int MaxRows = 24;

    /// <summary>Trần số người nhận của MỘT ô (To hoặc CC).</summary>
    public const int MaxRecipientsPerCell = 8;

    /// <summary>
    /// Trần số mục của danh sách người nhận. Rộng hơn bản gieo (4 quan hệ + tối đa 8 vai trò) vì người dùng
    /// còn tự thêm được, nhưng vẫn phải có trần: MỖI ô To/CC render đủ cả danh sách, nên 24 dòng × 2 ô ×
    /// từng mục là số checkbox thật nằm trong DOM.
    /// </summary>
    public const int MaxRecipientOptions = 20;

    private const int MaxTextChars = 200;
    private const int MaxEvidenceChars = 300;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Các DÒNG của bảng, gieo từ vòng đời trạng thái của bảng đối tượng ĐÃ CHỐT: mỗi trạng thái của mỗi
    /// đối tượng còn tích là một sự kiện. Rỗng ⇒ dự án không có vòng đời nào (ứng dụng danh mục thuần) và
    /// bảng này KHÔNG bao giờ được bày — xem <see cref="NotificationMapGate"/>.
    ///
    /// <para>
    /// Dòng gieo ra để cột To/CC RỖNG: "ai được báo" là câu hỏi của chính bảng này, và điền sẵn một phỏng
    /// đoán vào đó là ký tên người dùng vào danh sách người nhận mà họ chưa chọn. Ô điền sẵn duy nhất được
    /// phép là ô có trích dẫn thật, và nó đến từ lượt model bày bảng chứ không từ bảng đối tượng.
    /// </para>
    /// </summary>
    public static List<NotificationMapRow> SeedRows(string? entityMapJson)
    {
        var rows = new List<NotificationMapRow>();

        foreach (var entity in EntityMapBuilder.Parse(entityMapJson).Where(e => e.Included))
        {
            foreach (var state in entity.States)
            {
                if (string.IsNullOrWhiteSpace(state.State))
                    continue;

                rows.Add(new NotificationMapRow
                {
                    Entity = Clip(entity.Entity.Trim(), MaxTextChars),
                    Event = Clip(state.State.Trim(), MaxTextChars),
                    Trigger = Clip((state.EntryCondition ?? string.Empty).Trim(), MaxTextChars),
                    To = new List<string>()
                });

                if (rows.Count >= MaxRows)
                    return rows;
            }
        }

        return rows;
    }

    /// <summary>
    /// DANH SÁCH NGƯỜI NHẬN của dự án — nguồn của mọi ô To/CC, và của bảng "Danh sách người nhận" mà người
    /// dùng sửa trực tiếp. Đã chốt lần nào (<paramref name="recipientsJson"/> có mục) thì dùng đúng bộ đã
    /// lưu; chưa thì gieo tất định từ <paramref name="roles"/>.
    ///
    /// <para>
    /// Bộ đã lưu THẮNG bản gieo, kể cả khi bảng phân quyền sau đó đổi: nó là thứ người dùng đã tự tay rà,
    /// còn bản gieo chỉ là phỏng đoán. Đây cũng là bộ mà đường GỬI đối chiếu, nên hai đường (bày bảng và
    /// lưu bảng) phải gọi cùng hàm này với cùng đầu vào — lệch nhau là người dùng chọn được một mục rồi bị
    /// server bỏ đúng mục đó.
    /// </para>
    /// </summary>
    public static List<string> RecipientOptions(string? recipientsJson, IReadOnlyList<string>? roles)
    {
        var saved = SanitizeRecipients(ParseRecipients(recipientsJson));
        return saved.Count > 0 ? saved : SeedRecipients(roles);
    }

    /// <summary>
    /// Bản GIEO của danh sách người nhận, dùng khi dự án chưa chốt bảng thông báo lần nào: bốn mục QUAN HỆ
    /// trước (ca thường gặp — xem <see cref="NotificationRecipient"/>), rồi mỗi vai trò của bảng phân quyền
    /// đã chốt.
    ///
    /// <para>
    /// Vai trò được gieo NGUYÊN TÊN, không còn tiền tố "Toàn bộ ". Đó là quyết định của người dùng sản
    /// phẩm: không thao tác nào ở đây cần gửi email cho cả một vai trò, nên mục "Toàn bộ …" chỉ là một cái
    /// bẫy bấm nhầm. Đổi lại, một mục vai trò trần không tự nói được nó là một người hay cả nhóm — bảng
    /// danh sách người nhận là chỗ người dùng viết lại nó cho đúng, và
    /// <see cref="RenderConfirmedBlock"/> cấm các tầng sau tự suy rộng.
    /// </para>
    /// </summary>
    public static List<string> SeedRecipients(IReadOnlyList<string>? roles)
        => SanitizeRecipients(NotificationRecipient.Related.Concat(roles ?? Array.Empty<string>()));

    /// <summary>
    /// Chuẩn hoá một danh sách người nhận ĐẾN TỪ TRÌNH DUYỆT (hoặc từ cột đã lưu): bỏ mục rỗng, cắt theo
    /// trần độ dài, bỏ trùng theo cùng phép so khớp mà <see cref="MatchRecipient"/> dùng, chặn ở
    /// <see cref="MaxRecipientOptions"/>. Giữ nguyên THỨ TỰ người dùng sắp.
    ///
    /// <para>
    /// Bỏ trùng theo <see cref="Normalize"/> chứ không theo chuỗi thô là bắt buộc: "HRBP" và "hrbp " là hai
    /// dòng khác nhau trên bảng nhưng cùng một mục lúc so khớp, nên để cả hai lọt vào là dựng ra hai ô chọn
    /// không phân biệt được, mà giá trị chọn ra thì cùng đích.
    /// </para>
    /// </summary>
    public static List<string> SanitizeRecipients(IEnumerable<string>? recipients)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in recipients ?? Enumerable.Empty<string>())
        {
            var value = Clip((raw ?? string.Empty).Trim(), MaxTextChars);
            if (value.Length == 0 || !seen.Add(Normalize(value)))
                continue;

            result.Add(value);
            if (result.Count >= MaxRecipientOptions)
                break;
        }

        return result;
    }

    /// <summary>Đọc JSON danh sách người nhận đã lưu (cột DB hoặc payload client). null/rỗng/hỏng ⇒ mảng rỗng.</summary>
    public static List<string> ParseRecipients(string? json)
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

    /// <summary>
    /// Bảng cuối cùng cho lượt BA BÀY BẢNG: các dòng gieo sẵn theo đúng thứ tự
    /// (<paramref name="seedRows"/>), người nhận lấy từ đề xuất của model đã kéo về
    /// <paramref name="options"/>, cộng các dòng NHẮC NHỞ có trích dẫn mà model nêu thêm.
    ///
    /// <para>
    /// Mọi dòng ra khỏi đây đều TÍCH SẴN bất kể model trả gì: cờ tích là chỗ NGƯỜI DÙNG loại bớt, không
    /// phải chỗ model tự phủ nhận đề xuất của mình — structured output buộc điền đủ trường, nên một model
    /// điền <c>false</c> cho có sẽ âm thầm bỏ tích sạch bảng và người dùng gửi đi "không sự kiện nào cần
    /// báo" trong khi tưởng mình vừa xác nhận cả danh sách.
    /// </para>
    /// </summary>
    public static List<NotificationMapRow> Build(
        IEnumerable<NotificationMapRow>? proposed,
        IReadOnlyList<NotificationMapRow> seedRows,
        IReadOnlyList<string> options)
    {
        var result = new List<NotificationMapRow>();
        if (seedRows.Count == 0 || options.Count == 0)
            return result;

        var byKey = new Dictionary<string, NotificationMapRow>(StringComparer.Ordinal);
        var extras = new List<NotificationMapRow>();
        foreach (var row in proposed ?? Enumerable.Empty<NotificationMapRow>())
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Event))
                continue;

            var key = Key(row.Entity, row.Event);
            if (seedRows.Any(s => Key(s.Entity, s.Event) == key))
            {
                if (!byKey.ContainsKey(key))
                    byKey[key] = row;
            }
            else
            {
                extras.Add(row);
            }
        }

        foreach (var seed in seedRows)
        {
            byKey.TryGetValue(Key(seed.Entity, seed.Event), out var match);

            // Ô notify cũ của bảng đối tượng (nếu có) là điền sẵn NỀN; đề xuất tươi của model thắng khi nó
            // nói được điều gì. Cả hai đều đi qua MatchRecipient nên không giá trị nào ngoài danh sách chọn
            // lọt vào bảng.
            var to = NormalizeRecipients(match?.To?.Count > 0 ? match.To : seed.To, options, Array.Empty<string>());
            var cc = NormalizeRecipients(match?.Cc, options, to);
            var evidence = Clip((match?.Evidence ?? string.Empty).Trim(), MaxEvidenceChars);

            result.Add(new NotificationMapRow
            {
                Entity = seed.Entity,
                Event = seed.Event,
                Trigger = seed.Trigger,
                To = to,
                Cc = cc,
                Needed = true,
                // LUẬT BẰNG CHỨNG — cờ suông không khóa được dòng nào. Xem PermissionGrant.Locked.
                //
                // …và phải có CẢ người nhận. Dòng khóa không có checkbox trên giao diện (ô "Cần" là một
                // input hidden), nên "khóa mà To rỗng" là một ngõ chết: người dùng không được nói "sự kiện
                // này không cần gửi", mà cũng chẳng có ai để gửi. Ca sinh ra nó rất thường: model kèm
                // trích dẫn thật nhưng viết người nhận không khớp mục nào của danh sách chọn ⇒
                // NormalizeRecipients bỏ sạch To, evidence thì còn lại.
                Locked = evidence.Length > 0 && to.Count > 0,
                Evidence = evidence
            });
        }

        // Dòng NHẮC NHỞ model nêu thêm: chỉ đi qua khi có TRÍCH DẪN. Đây là đường cho "trước hạn 3 ngày",
        // "quá hạn mà chưa ai duyệt" — những thứ không phải chuyển trạng thái nên bảng đối tượng không gieo
        // ra được. Không có trích dẫn thì đó là một sự kiện model nghĩ thêm, và một dòng như vậy được người
        // dùng tích sẵn hộ rồi đi thẳng vào spec.
        foreach (var extra in extras)
        {
            if (result.Count >= MaxRows)
                break;

            var evidence = Clip((extra.Evidence ?? string.Empty).Trim(), MaxEvidenceChars);
            if (evidence.Length == 0)
                continue;

            var eventName = Clip(extra.Event.Trim(), MaxTextChars);
            if (result.Any(r => Key(r.Entity, r.Event) == Key(extra.Entity, eventName)))
                continue;

            var to = NormalizeRecipients(extra.To, options, Array.Empty<string>());
            result.Add(new NotificationMapRow
            {
                Entity = Clip((extra.Entity ?? string.Empty).Trim(), MaxTextChars),
                Event = eventName,
                Trigger = Clip((extra.Trigger ?? string.Empty).Trim(), MaxTextChars),
                To = to,
                Cc = NormalizeRecipients(extra.Cc, options, to),
                Needed = true,
                Locked = true,
                Evidence = evidence
            });
        }

        return result;
    }

    /// <summary>
    /// Bản chuẩn hoá cho dữ liệu ĐẾN TỪ TRÌNH DUYỆT: giữ đúng lựa chọn tích/bỏ tích của người dùng, xoá cờ
    /// khóa (bảng đã gửi thì mọi dòng là quyết định của họ), và vẫn kéo mọi người nhận về
    /// <paramref name="options"/> — server không tin payload nó vừa render ra vài giây trước.
    ///
    /// <para>
    /// Khác <see cref="Build"/> ở chỗ KHÔNG cần dòng gieo: dòng của bảng đã nằm trong payload, và dòng mang
    /// cờ <see cref="NotificationMapRow.AddedByUser"/> đi qua được dù không khớp chuyển trạng thái nào —
    /// chốt chặn "sự kiện bịa" dựng để chặn MODEL, không phải để chặn người dùng vừa tự gõ một lời nhắc.
    /// </para>
    /// </summary>
    public static List<NotificationMapRow> Sanitize(
        IEnumerable<NotificationMapRow>? submitted,
        IReadOnlyList<string> options)
    {
        var result = new List<NotificationMapRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in submitted ?? Enumerable.Empty<NotificationMapRow>())
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Event))
                continue;

            var eventName = Clip(row.Event.Trim(), MaxTextChars);
            var entity = Clip((row.Entity ?? string.Empty).Trim(), MaxTextChars);
            if (!seen.Add(Key(entity, eventName)))
                continue;

            // Dòng BỎ TÍCH không mang người nhận theo. Người dùng bỏ tích sau khi đã chọn vài mục là một
            // câu "sự kiện này không gửi cho ai cả" — giữ lại To/CC là lưu vào DB đúng cái danh sách họ
            // vừa hủy, rồi mọi tầng đọc sau phải tự nhớ lọc theo `Needed` mới không gửi thừa. Hai khối kể
            // lại đang lọc đúng, nhưng đó là thứ không nên phải đúng ở ba nơi.
            var to = row.Needed ? NormalizeRecipients(row.To, options, Array.Empty<string>()) : new List<string>();
            result.Add(new NotificationMapRow
            {
                Entity = entity,
                Event = eventName,
                Trigger = Clip((row.Trigger ?? string.Empty).Trim(), MaxTextChars),
                To = to,
                Cc = row.Needed ? NormalizeRecipients(row.Cc, options, to) : new List<string>(),
                Needed = row.Needed,
                // Cờ khóa bị xoá, cờ TỰ THÊM thì không: cái trước là lời khai của model về hội thoại (hết ý
                // nghĩa khi người dùng đã tự tay duyệt), cái sau là một sự thật về nguồn gốc của dòng mà
                // RenderUserMessage còn phải kể lại. Cùng luật với EntityMapBuilder.Sanitize.
                Locked = false,
                Evidence = string.Empty,
                AddedByUser = row.AddedByUser
            });

            if (result.Count >= MaxRows)
                break;
        }

        return result;
    }

    /// <summary>Đọc JSON bảng thông báo đã lưu (cột DB hoặc payload client). null/rỗng/hỏng ⇒ mảng rỗng.</summary>
    public static List<NotificationMapRow> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<NotificationMapRow>();

        try
        {
            var rows = JsonSerializer.Deserialize<List<NotificationMapRow>>(json, ReadOptions)
                ?? new List<NotificationMapRow>();
            return rows.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Event)).ToList();
        }
        catch
        {
            return new List<NotificationMapRow>();
        }
    }

    /// <summary>Dự án này đã chốt bảng thông báo chưa.</summary>
    public static bool IsConfirmed(string? json) => Parse(json).Count > 0;

    /// <summary>
    /// Các dòng VI PHẠM bất biến của bảng: còn tích "Cần" mà chưa chọn người nhận chính. Rỗng ⇒ bảng lưu
    /// được. Đây là chốt chặn THẬT của bất biến (xem phần <b>BẤT BIẾN</b> ở doc của class): popup trên
    /// trình duyệt chỉ là phanh phụ, còn một payload sửa tay, một tab cũ mở từ trước bản này, hay một lần
    /// thử lại sau khi mất mạng đều đi vào đúng đây.
    ///
    /// <para>
    /// Cố tình KHÔNG "tự chữa" bằng cách hạ <c>Needed = false</c>: bỏ tích là một quyết định nghiệp vụ
    /// ("sự kiện này không cần email"), và tự quyết hộ nó là ghi vào tài liệu một điều người dùng chưa nói
    /// — đúng loại lỗi nặng nhất của cả tầng phỏng vấn này. Không hợp lệ thì KHÔNG lưu, và nói ra thiếu ở
    /// đâu.
    /// </para>
    /// </summary>
    public static List<NotificationMapRow> MissingRecipients(IEnumerable<NotificationMapRow>? rows)
        => (rows ?? Enumerable.Empty<NotificationMapRow>())
            .Where(r => r != null && r.Needed && r.To.Count == 0)
            .ToList();

    /// <summary>
    /// Tên một sự kiện như người dùng đọc thấy trên bảng ("Đơn đăng ký — "Chờ duyệt" (khi nhân viên gửi
    /// đơn)"). Dùng cho thông điệp lỗi của đường gửi: gọi tên đúng dòng còn thiếu thì người dùng biết phải
    /// sửa ở đâu, còn một câu "bảng chưa hợp lệ" thì bắt họ tự rà lại 24 dòng.
    /// </summary>
    public static string EventLabel(NotificationMapRow row) => RenderEvent(row);

    /// <summary>
    /// Khối ngữ cảnh gắn vào MỌI lượt chat sau khi bảng đã chốt, vào lượt distill bản đồ bao phủ, và vào
    /// prompt sinh AI Design Spec. Trả null khi chưa chốt.
    ///
    /// <para>
    /// Khối này là một lệnh CẤM HỎI TUYỆT ĐỐI cho cả nhóm, không còn ngoại lệ nào: bất biến của bảng (xem
    /// doc của class) bảo đảm mọi dòng đã lưu đều đã trả lời xong — hoặc "không gửi", hoặc có người nhận.
    /// Hai loại đều được GỌI TÊN ở đây, vì mặc định im lặng của mọi tầng phía sau là "có thay đổi thì báo":
    /// một sự kiện người dùng vừa tắt mà khối này không nhắc tới sẽ được bước soạn tài liệu bật lại.
    /// </para>
    /// </summary>
    public static string? RenderConfirmedBlock(string? json)
    {
        var rows = Parse(json);
        if (rows.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng thông báo / nhắc nhở đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại) ---");
        sb.AppendLine("Mỗi dòng: sự kiện · người nhận chính (To) · người nhận đồng gửi (CC). Kênh gửi duy nhất "
            + "của nền tảng là EMAIL nên không có cột kênh. Người nhận là các mục của DANH SÁCH NGƯỜI NHẬN do "
            + "chính người dùng chốt: hoặc một QUAN HỆ với bản ghi (\"Người tạo\", \"Quản lý trực tiếp của "
            + "người tạo\"), hoặc tên một người/nhóm họ tự viết ra. TUYỆT ĐỐI không tự suy rộng một mục thành "
            + "\"mọi người mang vai đó\" — không sự kiện nào ở đây gửi email cho cả một vai trò.");

        foreach (var row in rows.Where(r => r.Needed && r.To.Count > 0))
            sb.AppendLine("* " + RenderRow(row));

        var silent = rows.Where(r => !r.Needed).ToList();
        if (silent.Count > 0)
            sb.AppendLine("* KHÔNG gửi thông báo ở các sự kiện sau — đây là quyết định của người dùng, không "
                + "phải chỗ còn thiếu: " + string.Join("; ", silent.Select(RenderEvent)));

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Tin nhắn mà TRÌNH DUYỆT gửi tiếp vào khung chat sau khi bảng đã lưu — cùng khuôn hai bước với các
    /// bảng khác, và soạn ở server vì cùng lý do: bản kể phải khớp đúng bản đã lưu.
    ///
    /// <para>
    /// Câu mở đầu phải nói đúng thứ bảng thật sự chở. Bản trước mở bằng <i>"đây là các sự kiện cần gửi email
    /// và người nhận"</i> rồi mới đính chính ở đoạn thứ ba rằng 7/8 sự kiện chưa có người nhận — người dùng
    /// đọc cái tiêu đề và tin là mình đã trả lời xong, nên mọi câu BA hỏi tiếp (đúng luật) trông như hỏi lại
    /// điều vừa nói. Bất biến mới xóa hẳn ca đó, nhưng câu mở đầu vẫn phải phân biệt "có sự kiện cần gửi"
    /// với "người dùng tắt sạch".
    /// </para>
    /// </summary>
    public static string RenderUserMessage(IReadOnlyList<NotificationMapRow> rows)
    {
        if (rows.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        var sending = rows.Where(r => r.Needed && r.To.Count > 0).ToList();

        if (sending.Count > 0)
        {
            sb.AppendLine("Mình đã rà bảng thông báo — đây là các sự kiện cần gửi email và người nhận:");
            foreach (var row in sending)
                sb.AppendLine("- " + RenderRow(row));
        }
        else
        {
            sb.AppendLine("Mình đã rà bảng thông báo — không sự kiện nào cần gửi email.");
        }

        // Sự kiện bị bỏ tích phải được GỌI TÊN, cùng lý do với dòng bị bỏ tích của các bảng khác: im lặng bỏ
        // nó đi thì người dùng không có bằng chứng nào cho thấy mình vừa tắt đúng thứ định tắt, mà mặc định
        // im lặng của mọi tầng phía sau lại là "có thay đổi thì báo".
        var silent = rows.Where(r => !r.Needed).ToList();
        if (silent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Các sự kiện KHÔNG cần gửi thông báo: " + string.Join("; ", silent.Select(RenderEvent)) + ".");
        }

        var addedByUser = rows.Where(r => r.AddedByUser).ToList();
        if (addedByUser.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Các sự kiện/lời nhắc mình tự bổ sung vào bảng: "
                + string.Join("; ", addedByUser.Select(RenderEvent)) + ".");
        }

        return sb.ToString().TrimEnd();
    }

    // ==== chuẩn hoá từng phần ====

    private static string RenderEvent(NotificationMapRow row)
    {
        var entity = string.IsNullOrWhiteSpace(row.Entity) ? string.Empty : $"{row.Entity.Trim()} — ";
        var trigger = string.IsNullOrWhiteSpace(row.Trigger) ? string.Empty : $" (khi {row.Trigger.Trim()})";
        return $"{entity}\"{row.Event.Trim()}\"{trigger}";
    }

    // Chỉ gọi cho dòng CÓ người nhận (cả hai bản kể đều lọc trước) — bất biến của bảng bảo đảm không còn
    // dòng "cần gửi mà To rỗng" nào để phải kể.
    private static string RenderRow(NotificationMapRow row)
    {
        var cc = row.Cc.Count > 0 ? $"; CC: {string.Join(", ", row.Cc)}" : string.Empty;
        return $"{RenderEvent(row)} ⇒ To: {string.Join(", ", row.To)}{cc}";
    }

    private static string Key(string? entity, string? eventName)
        => Normalize(entity ?? string.Empty) + "|" + Normalize(eventName ?? string.Empty);

    /// <summary>
    /// Kéo các giá trị đề xuất về đúng danh sách chọn, bỏ trùng, bỏ những mục đã có ở
    /// <paramref name="exclude"/> (một người vừa To vừa CC là một người nhận hai email của cùng một sự kiện)
    /// và chặn ở trần <see cref="MaxRecipientsPerCell"/>.
    /// </summary>
    private static List<string> NormalizeRecipients(
        IEnumerable<string>? proposed, IReadOnlyList<string> options, IReadOnlyList<string> exclude)
    {
        var result = new List<string>();
        var seen = exclude.Select(Normalize).ToHashSet(StringComparer.Ordinal);

        foreach (var raw in proposed ?? Enumerable.Empty<string>())
        {
            var option = MatchRecipient(raw, options);
            if (option == null || !seen.Add(Normalize(option)))
                continue;

            result.Add(option);
            if (result.Count >= MaxRecipientsPerCell)
                break;
        }

        return result;
    }

    /// <summary>Ngưỡng độ dài cho phép so khớp CHỨA-NHAU — mẩu ngắn hơn dính vào quá nhiều mục.</summary>
    private const int MinContainsLength = 5;

    /// <summary>
    /// Ghép một giá trị model (hoặc trình duyệt) đưa lên về đúng một mục của danh sách người nhận. Hai nấc,
    /// hẹp dần: khớp CHÍNH XÁC → khớp chứa-nhau và chỉ khi có ĐÚNG MỘT mục dài nhất khớp. Không khớp ⇒ null
    /// và giá trị bị bỏ: ô hiện ít lựa chọn sẵn hơn thì người dùng tự chọn nốt (hoặc tự thêm mục vào bảng
    /// danh sách người nhận), còn một người nhận model bịa thì đi thẳng vào spec mà không ai đọc lại bảng
    /// nhận ra được.
    ///
    /// <para>
    /// Nấc chứa-nhau còn đang gánh thêm một việc: các dự án chốt bảng đối tượng/thông báo từ bản có tiền tố
    /// "Toàn bộ " mang giá trị cũ ("Toàn bộ Manager") lên đây, và nấc này kéo chúng về đúng mục vai trò
    /// trần ("Manager") thay vì bỏ sạch người nhận của cả bảng.
    /// </para>
    /// </summary>
    private static string? MatchRecipient(string? proposed, IReadOnlyList<string> options)
    {
        var value = Normalize(proposed ?? string.Empty);
        if (value.Length == 0)
            return null;

        foreach (var option in options)
        {
            if (Normalize(option) == value)
                return option;
        }

        if (value.Length < MinContainsLength)
            return null;

        var matches = options.Where(o =>
        {
            var normalized = Normalize(o);
            return normalized.Contains(value, StringComparison.Ordinal)
                || (normalized.Length >= MinContainsLength && value.Contains(normalized, StringComparison.Ordinal));
        }).OrderByDescending(o => Normalize(o).Length).ToList();

        // Hai mục dài bằng nhau cùng khớp ⇒ mơ hồ, bỏ hẳn. Gán bừa vào mục đầu tiên là gửi email cho nhầm
        // người, thứ không ai phát hiện được khi đọc lại bảng.
        if (matches.Count == 0)
            return null;
        if (matches.Count > 1 && Normalize(matches[0]).Length == Normalize(matches[1]).Length)
            return null;

        return matches[0];
    }

    private static string Normalize(string value)
        => string.Join(' ', (value ?? string.Empty).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim(' ', '.', ',', ':', ';', '-', '–');

    private static string Clip(string value, int max)
        => value.Length > max ? value[..max] : value;
}
