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
//  • NGƯỜI NHẬN phải nằm trong danh sách chọn đóng: một người nhận bịa đi thẳng vào spec rồi vào POC mà
//    không ai đọc lại bảng nhận ra được;
//  • cờ "Cần" ở lượt BÀY BẢNG luôn TÍCH SẴN bất kể model trả gì;
//  • ba trạng thái của một dòng — gửi / không gửi / cần gửi mà chưa chọn ai — phải phân biệt được ở cả
//    khối ngữ cảnh lẫn tin nhắn gửi đi, vì mặc định im lặng của mọi tầng phía sau là "có thay đổi thì báo".
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
        NotificationMapBuilder.RecipientOptions(new List<string> { "Manager", "HRBP" });

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

        Assert.Equal(new[] { "Toàn bộ Manager" }, rows[0].To);
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
            new NotificationMapRow { Event = "Nhắc hàng tuần", To = { "Toàn bộ HRBP" } }
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

    // ==== NGƯỜI NHẬN: danh sách chọn ĐÓNG ====

    [Fact]
    public void RecipientOptions_PutsRelationsFirstThenWholeRoles()
    {
        Assert.Equal(new[]
        {
            "Người tạo", "Người được phân công", "Quản lý trực tiếp của người tạo", "HOD của đơn vị liên quan",
            "Toàn bộ Manager", "Toàn bộ HRBP"
        }, Options);
    }

    // Model hay viết tên vai trần ("Manager") thay vì mục đầy đủ. Kéo về đúng mục thay vì bỏ — nhưng chỉ
    // theo đúng phần TÊN VAI, không đoán mò.
    [Fact]
    public void Build_SnapsABareRoleNameOntoTheWholeRoleOption()
    {
        var rows = NotificationMapBuilder.Build(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", To = { "Manager" } }
        }, Seeds(), Options);

        Assert.Equal(new[] { "Toàn bộ Manager" }, rows.Single(r => r.Event == "Đã duyệt").To);
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
                Cc = { "Người tạo", "Toàn bộ HRBP" }
            }
        }, Seeds(), Options);

        Assert.Equal(new[] { "Toàn bộ HRBP" }, rows.Single(r => r.Event == "Đã duyệt").Cc);
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

    // ==== BA TRẠNG THÁI CỦA MỘT DÒNG ====

    // "Không gửi" là một QUYẾT ĐỊNH và phải nói ra được; "cần gửi nhưng chưa chọn ai" là chỗ DUY NHẤT BA
    // còn được phép hỏi lại sau khi bảng đã chốt. Lẫn hai thứ đó là hoặc bỏ quên một câu hỏi thật, hoặc
    // hỏi lại đúng thứ người dùng vừa tắt.
    [Fact]
    public void ConfirmedBlock_SeparatesSilentEventsFromUnansweredOnes()
    {
        var json = JsonSerializer.Serialize(NotificationMapBuilder.Sanitize(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", Needed = true, To = { "Người tạo" } },
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Chờ duyệt", Needed = false },
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Bị từ chối", Needed = true }
        }, Options));

        var block = NotificationMapBuilder.RenderConfirmedBlock(json);

        Assert.Contains("To: Người tạo", block);
        Assert.Contains("KHÔNG gửi thông báo ở các sự kiện sau", block);
        Assert.Contains("CHƯA chọn người nhận", block);
        Assert.Contains("Bị từ chối", block);
    }

    [Fact]
    public void UserMessage_NamesTheEventsTheUserTurnedOff()
    {
        var rows = NotificationMapBuilder.Sanitize(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", Needed = true, To = { "Người tạo" }, Cc = { "Toàn bộ HRBP" } },
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Chờ duyệt", Needed = false }
        }, Options);

        var message = NotificationMapBuilder.RenderUserMessage(rows);

        Assert.Contains("To: Người tạo; CC: Toàn bộ HRBP", message);
        Assert.Contains("KHÔNG cần gửi thông báo", message);
        Assert.Contains("Chờ duyệt", message);
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
