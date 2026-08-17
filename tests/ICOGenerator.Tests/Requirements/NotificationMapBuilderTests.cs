using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Builder của BẢNG THÔNG BÁO — bảng cuối cùng của buổi phỏng vấn. Cùng họ chốt chặn với
// PermissionMatrixBuilder, nên test nhắm đúng các đường hỏng đã biết:
//
//  • DÒNG do cơ chế gieo từ bảng đối tượng, không do model liệt kê — sự kiện model quên nêu vẫn phải có
//    mặt, còn sự kiện model nghĩ thêm thì phải có TRÍCH DẪN mới đi qua;
//  • NGƯỜI NHẬN phải nằm trong DANH SÁCH NGƯỜI NHẬN của dự án — danh sách đó người dùng sửa được, model
//    thì không: một người nhận model bịa đi thẳng vào spec rồi vào POC mà không ai đọc lại bảng nhận ra;
//  • cờ "Cần" ở lượt BÀY BẢNG luôn TÍCH SẴN bất kể model trả gì;
//  • BẤT BIẾN của bảng đã lưu: một dòng chỉ có HAI trạng thái — bỏ tích ("không gửi") hoặc còn tích KÈM
//    người nhận. Cả hai đều phải được GỌI TÊN ở khối ngữ cảnh và tin nhắn gửi đi, vì mặc định im lặng của
//    mọi tầng phía sau là "có thay đổi thì báo"; còn trạng thái thứ ba ("cần gửi mà chưa chọn ai") bị chặn
//    ở đường gửi thay vì được lưu rồi đem đi hỏi lại từng sự kiện trong khung chat.
public class NotificationMapBuilderTests
{
    private const string ConfirmedEntities = """
        [{"entity":"Đơn đăng ký","description":"Đơn của nhân viên","included":true,
          "fields":[{"name":"Người gửi","meaning":"Người lập đơn","used":true}],
          "states":[{"state":"Chờ duyệt","entryCondition":"nhân viên gửi đơn"},
                    {"state":"Đã duyệt","entryCondition":"quản lý bấm duyệt"},
                    {"state":"Bị từ chối","entryCondition":"quản lý từ chối"}]},
         {"entity":"Khóa học","description":"Danh mục khóa","included":true,
          "fields":[{"name":"Tên khóa","meaning":"Tên hiển thị","used":true}],"states":[]}]
        """;

    private static readonly List<string> Options =
        NotificationMapBuilder.RecipientOptions(null, new List<string> { "Manager", "HRBP" });

    private static List<NotificationMapRow> Seeds() => NotificationMapBuilder.SeedRows(ConfirmedEntities);

    // ==== DÒNG: gieo từ vòng đời của bảng đối tượng ====

    [Fact]
    public void SeedRows_TakesOneRowPerLifecycleState()
    {
        var rows = Seeds();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("Đơn đăng ký", r.Entity));
        Assert.Equal(new[] { "Chờ duyệt", "Đã duyệt", "Bị từ chối" }, rows.Select(r => r.Event));
        Assert.Equal("quản lý bấm duyệt", rows[1].Trigger);
    }

    // Đối tượng danh mục không có vòng đời ⇒ không sự kiện nào. Dự án toàn danh mục ⇒ bảng KHÔNG bày ra
    // (NotificationMapGate), và nhóm quay về đường hỏi bằng câu hỏi.
    [Fact]
    public void SeedRows_IgnoresEntitiesWithoutALifecycle()
    {
        var catalogOnly = """[{"entity":"Khóa học","included":true,"states":[]}]""";

        Assert.Empty(NotificationMapBuilder.SeedRows(catalogOnly));
    }

    // Ô "báo cho ai" của bản TRƯỚC (khi thông báo còn là một ô text cạnh từng trạng thái) được kéo qua
    // thành giá trị điền sẵn: người dùng đã trả lời đúng câu đó rồi, chỉ khác là trả lời bằng cách gõ.
    [Fact]
    public void Build_CarriesTheLegacyNotifyColumnIntoTheToCell()
    {
        var legacy = """
            [{"entity":"Đơn đăng ký","included":true,
              "states":[{"state":"Chờ duyệt","entryCondition":"gửi đơn","notify":"Manager"},
                        {"state":"Đã duyệt","entryCondition":"duyệt","notify":""}]}]
            """;

        var rows = NotificationMapBuilder.Build(null, NotificationMapBuilder.SeedRows(legacy), Options);

        Assert.Equal(new[] { "Manager" }, rows[0].To);
        Assert.Empty(rows[1].To);
    }

    // Model chỉ nêu một sự kiện nó nhớ ⇒ hai sự kiện còn lại VẪN phải có mặt, ở trạng thái chưa chọn người
    // nhận. Im lặng bỏ chúng đi là biến "chưa hỏi" thành "không báo cho ai" mà người dùng không bao giờ
    // nhìn thấy để phản đối.
    [Fact]
    public void Build_KeepsEventsTheModelForgot()
    {
        var rows = NotificationMapBuilder.Build(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", To = { "Người tạo" } }
        }, Seeds(), Options);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "Người tạo" }, rows.Single(r => r.Event == "Đã duyệt").To);
        Assert.Empty(rows.Single(r => r.Event == "Chờ duyệt").To);
    }

    // Cờ "Cần" là chỗ NGƯỜI DÙNG loại bớt, không phải chỗ model tự phủ nhận đề xuất của mình: structured
    // output buộc điền đủ trường nên một model điền false cho có sẽ âm thầm tắt sạch bảng.
    [Fact]
    public void Build_TicksEveryRowRegardlessOfWhatTheModelSaid()
    {
        var rows = NotificationMapBuilder.Build(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", Needed = false }
        }, Seeds(), Options);

        Assert.All(rows, r => Assert.True(r.Needed));
    }

    // Dòng NHẮC NHỞ ("trước hạn 3 ngày") không phải chuyển trạng thái nào nên bảng đối tượng không gieo ra
    // được — model được phép thêm, nhưng CHỈ khi có trích dẫn thật.
    [Fact]
    public void Build_AcceptsAnExtraReminderOnlyWithEvidence()
    {
        var rows = NotificationMapBuilder.Build(new[]
        {
            new NotificationMapRow
            {
                Event = "Sắp tới hạn duyệt",
                Trigger = "trước hạn 3 ngày",
                To = { "Người được phân công" },
                Evidence = "quá 3 ngày chưa duyệt thì nhắc lại"
            },
            new NotificationMapRow { Event = "Nhắc hàng tuần", To = { "HRBP" } }
        }, Seeds(), Options);

        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, r => r.Event == "Sắp tới hạn duyệt" && r.Locked);
        Assert.DoesNotContain(rows, r => r.Event == "Nhắc hàng tuần");
    }

    // LUẬT BẰNG CHỨNG: cờ suông không khóa được dòng nào.
    [Fact]
    public void Build_DoesNotLockARowWithoutAQuote()
    {
        var rows = NotificationMapBuilder.Build(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", Locked = true, To = { "Người tạo" } }
        }, Seeds(), Options);

        Assert.False(rows.Single(r => r.Event == "Đã duyệt").Locked);
    }

    // NGÕ CHẾT đã gặp thật: model kèm trích dẫn NHƯNG viết người nhận không khớp mục nào ⇒ To bị bỏ sạch,
    // evidence còn lại. Dòng khóa không có checkbox trên giao diện, nên nếu vẫn khóa thì người dùng vừa
    // không được nói "sự kiện này không cần gửi", vừa chẳng có ai để gửi — mà giờ đường gửi lại đòi mọi
    // dòng còn tích phải có người nhận.
    [Fact]
    public void Build_DoesNotLockARowWhoseRecipientWasDropped()
    {
        var rows = NotificationMapBuilder.Build(new[]
        {
            new NotificationMapRow
            {
                Entity = "Đơn đăng ký",
                Event = "Đã duyệt",
                To = { "Phòng Nhân sự" },
                Evidence = "gửi cho phòng nhân sự"
            }
        }, Seeds(), Options);

        var row = rows.Single(r => r.Event == "Đã duyệt");
        Assert.Empty(row.To);
        Assert.False(row.Locked);
    }

    // ==== NGƯỜI NHẬN: DANH SÁCH của dự án ====

    // Bản GIEO cho dự án chưa từng sửa danh sách: bốn quan hệ trước (ca thường gặp), rồi các vai trò của
    // bảng phân quyền đã chốt, nguyên tên.
    [Fact]
    public void RecipientOptions_SeedsRelationsFirstThenTheConfirmedRoles()
    {
        Assert.Equal(new[]
        {
            "Người tạo", "Người được phân công", "Quản lý trực tiếp của người tạo", "HOD của đơn vị liên quan",
            "Manager", "HRBP"
        }, Options);
    }

    // Bộ ĐÃ LƯU thắng bản gieo, kể cả khi bảng phân quyền sau đó đổi: nó là thứ người dùng đã tự tay rà
    // (thêm "Ban giám đốc", xóa các vai họ không gửi email), còn bản gieo chỉ là phỏng đoán.
    [Fact]
    public void RecipientOptions_PrefersTheSavedListOverTheSeed()
    {
        var options = NotificationMapBuilder.RecipientOptions(
            """["Người tạo","Ban giám đốc"]""", new List<string> { "Manager", "HRBP" });

        Assert.Equal(new[] { "Người tạo", "Ban giám đốc" }, options);
    }

    // Danh sách đến từ trình duyệt: bỏ mục rỗng và bỏ TRÙNG theo cùng phép so khớp mà MatchRecipient dùng —
    // "HRBP" với " hrbp " là hai dòng khác nhau trên bảng nhưng cùng một mục lúc chọn, nên để cả hai lọt
    // vào là dựng ra hai tùy chọn không phân biệt được mà cùng đích.
    [Fact]
    public void SanitizeRecipients_DropsBlanksAndCaseOnlyDuplicates()
    {
        var list = NotificationMapBuilder.SanitizeRecipients(
            new[] { "Người tạo", "  ", " hrbp ", "HRBP", "Ban giám đốc" });

        Assert.Equal(new[] { "Người tạo", "hrbp", "Ban giám đốc" }, list);
    }

    // Dự án đã chốt bảng từ bản CÓ tiền tố "Toàn bộ " mang giá trị cũ lên: nấc khớp chứa-nhau kéo nó về
    // đúng mục vai trò trần thay vì bỏ sạch người nhận của cả bảng.
    [Fact]
    public void Build_SnapsALegacyWholeRoleValueOntoTheRoleOption()
    {
        var rows = NotificationMapBuilder.Build(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", To = { "Toàn bộ Manager" } }
        }, Seeds(), Options);

        Assert.Equal(new[] { "Manager" }, rows.Single(r => r.Event == "Đã duyệt").To);
    }

    // Người nhận không khớp mục nào bị BỎ. Ô hiện ít lựa chọn sẵn hơn thì người dùng tự chọn nốt; một
    // người nhận bịa thì đi thẳng vào spec mà không ai đọc lại bảng nhận ra được.
    [Fact]
    public void Build_DropsARecipientOutsideTheOptionList()
    {
        var rows = NotificationMapBuilder.Build(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", To = { "Phòng Nhân sự" } }
        }, Seeds(), Options);

        Assert.Empty(rows.Single(r => r.Event == "Đã duyệt").To);
    }

    // Một người vừa To vừa CC là một người nhận HAI email của cùng một sự kiện.
    [Fact]
    public void Build_DoesNotRepeatAToRecipientInCc()
    {
        var rows = NotificationMapBuilder.Build(new[]
        {
            new NotificationMapRow
            {
                Entity = "Đơn đăng ký",
                Event = "Đã duyệt",
                To = { "Người tạo" },
                Cc = { "Người tạo", "HRBP" }
            }
        }, Seeds(), Options);

        Assert.Equal(new[] { "HRBP" }, rows.Single(r => r.Event == "Đã duyệt").Cc);
    }

    // ==== ĐƯỜNG GỬI ====

    // Server không tin payload nó vừa render ra: người nhận vẫn phải khớp danh sách chọn dựng LẠI từ bảng
    // phân quyền đã chốt.
    [Fact]
    public void Sanitize_RespectsTheUserSelectionButStillFiltersRecipients()
    {
        var rows = NotificationMapBuilder.Sanitize(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Chờ duyệt", Needed = false },
            new NotificationMapRow
            {
                Entity = "Đơn đăng ký",
                Event = "Đã duyệt",
                Needed = true,
                To = { "Người tạo", "Giám đốc nhà máy" },
                Locked = true,
                Evidence = "model tự khai"
            }
        }, Options);

        Assert.False(rows[0].Needed);
        Assert.Equal(new[] { "Người tạo" }, rows[1].To);
        // Bảng đã gửi thì mọi dòng là quyết định của người dùng — cờ khóa hết ý nghĩa.
        Assert.False(rows[1].Locked);
        Assert.Empty(rows[1].Evidence);
    }

    // Bỏ tích sau khi đã chọn vài mục là một câu "sự kiện này không gửi cho ai cả". Giữ lại To/CC là lưu
    // đúng cái danh sách người dùng vừa hủy, rồi bắt mọi tầng đọc sau tự nhớ lọc theo `Needed`.
    [Fact]
    public void Sanitize_ClearsTheRecipientsOfAnUntickedRow()
    {
        var rows = NotificationMapBuilder.Sanitize(new[]
        {
            new NotificationMapRow
            {
                Entity = "Đơn đăng ký",
                Event = "Chờ duyệt",
                Needed = false,
                To = { "Người tạo" },
                Cc = { "HRBP" }
            }
        }, Options);

        var row = Assert.Single(rows);
        Assert.Empty(row.To);
        Assert.Empty(row.Cc);
    }

    // Chốt chặn "sự kiện bịa" dựng để chặn MODEL, không phải để chặn người dùng vừa tự gõ một lời nhắc.
    [Fact]
    public void Sanitize_KeepsAReminderTheUserAddedWithoutAnyEvidence()
    {
        var rows = NotificationMapBuilder.Sanitize(new[]
        {
            new NotificationMapRow { Event = "Nhắc trước hạn 3 ngày", AddedByUser = true, To = { "Người tạo" } }
        }, Options);

        Assert.True(Assert.Single(rows).AddedByUser);
    }

    // ==== BẤT BIẾN: HAI TRẠNG THÁI CỦA MỘT DÒNG ====

    // Chốt chặn THẬT của bất biến, ở đường gửi. Trạng thái thứ ba từng được lưu và trả giá đúng bằng thứ
    // cái bảng sinh ra để thay thế: nhóm xuống [MỘT PHẦN], nút "Write Requirement" khóa, BA đi hỏi lại từng
    // sự kiện trong chat — 14 lượt cho một bảng 8 dòng gửi đi với 7 dòng trống.
    [Fact]
    public void MissingRecipients_FindsTickedRowsWithoutARecipient()
    {
        var rows = NotificationMapBuilder.Sanitize(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", Needed = true, To = { "Người tạo" } },
            new NotificationMapRow
            {
                Entity = "Đơn đăng ký", Event = "Bị từ chối", Trigger = "quản lý từ chối", Needed = true
            },
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Chờ duyệt", Needed = false }
        }, Options);

        var missing = NotificationMapBuilder.MissingRecipients(rows);

        // Dòng bỏ tích KHÔNG phải chỗ còn thiếu — nó là một quyết định.
        Assert.Equal(new[] { "Bị từ chối" }, missing.Select(r => r.Event));
        Assert.Equal("Đơn đăng ký — \"Bị từ chối\" (khi quản lý từ chối)",
            NotificationMapBuilder.EventLabel(missing[0]));
    }

    // "Không gửi" là một QUYẾT ĐỊNH và phải nói ra được: mặc định im lặng của mọi tầng phía sau là "có thay
    // đổi thì báo", nên một sự kiện người dùng vừa tắt mà khối này không nhắc tới sẽ được bật lại ở bước
    // soạn tài liệu.
    [Fact]
    public void ConfirmedBlock_NamesTheEventsTheUserTurnedOff()
    {
        var json = JsonSerializer.Serialize(NotificationMapBuilder.Sanitize(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", Needed = true, To = { "Người tạo" } },
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Chờ duyệt", Needed = false }
        }, Options));

        var block = NotificationMapBuilder.RenderConfirmedBlock(json);

        Assert.Contains("To: Người tạo", block);
        Assert.Contains("KHÔNG gửi thông báo ở các sự kiện sau", block);
        Assert.Contains("Chờ duyệt", block);
        // Khối là một lệnh cấm hỏi TUYỆT ĐỐI: không còn ngoại lệ nào mời BA hỏi lại nhóm này.
        Assert.DoesNotContain("CHƯA chọn người nhận", block);
    }

    [Fact]
    public void UserMessage_NamesTheEventsTheUserTurnedOff()
    {
        var rows = NotificationMapBuilder.Sanitize(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", Needed = true, To = { "Người tạo" }, Cc = { "HRBP" } },
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Chờ duyệt", Needed = false }
        }, Options);

        var message = NotificationMapBuilder.RenderUserMessage(rows);

        Assert.Contains("đây là các sự kiện cần gửi email và người nhận", message);
        Assert.Contains("To: Người tạo; CC: HRBP", message);
        Assert.Contains("KHÔNG cần gửi thông báo", message);
        Assert.Contains("Chờ duyệt", message);
    }

    // Người dùng tắt sạch bảng: câu mở đầu phải nói đúng điều đó. Bản trước mở bằng "đây là các sự kiện cần
    // gửi email và người nhận" ở MỌI ca rồi mới đính chính ở đoạn dưới — người dùng đọc tiêu đề và tin là
    // mình đã trả lời xong, nên mọi câu BA hỏi tiếp (đúng luật) trông như hỏi lại điều vừa nói.
    [Fact]
    public void UserMessage_SaysSoWhenTheUserTurnedEveryEventOff()
    {
        var rows = NotificationMapBuilder.Sanitize(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", Needed = false },
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Chờ duyệt", Needed = false }
        }, Options);

        var message = NotificationMapBuilder.RenderUserMessage(rows);

        Assert.Contains("không sự kiện nào cần gửi email", message);
        Assert.DoesNotContain("và người nhận:", message);
    }

    // Chưa chốt ⇒ không khối nào, và luồng chạy đúng như trước.
    [Fact]
    public void ConfirmedBlock_IsNullBeforeTheTableIsConfirmed()
    {
        Assert.Null(NotificationMapBuilder.RenderConfirmedBlock(null));
        Assert.Null(NotificationMapBuilder.RenderConfirmedBlock("[]"));
        Assert.False(NotificationMapBuilder.IsConfirmed("{ hỏng"));
    }
}
