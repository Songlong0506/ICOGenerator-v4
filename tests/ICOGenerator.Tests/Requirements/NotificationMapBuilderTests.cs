using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Builder của BẢNG THÔNG BÁO — nay chỉ còn phần ĐỌC cho các dự án cũ đã chốt bảng (bảng không còn được bày
// ra ở buổi phỏng vấn). Test giữ đúng hai thứ mà dữ liệu cũ còn đi qua:
//
//  • BẤT BIẾN của bảng đã lưu: một dòng chỉ có HAI trạng thái — bỏ tích ("không gửi") hoặc còn tích KÈM
//    người nhận. MissingRecipients nhận ra dữ liệu ghi trước khi bất biến đó tồn tại;
//  • khối ngữ cảnh GỌI TÊN cả hai loại, vì mặc định im lặng của mọi tầng phía sau là "có thay đổi thì báo".
public class NotificationMapBuilderTests
{
    [Fact]
    public void MissingRecipients_FindsTickedRowsWithoutARecipient()
    {
        var rows = new List<NotificationMapRow>
        {
            new() { Entity = "Đơn đăng ký", Event = "Đã duyệt", Needed = true, To = { "Người tạo" } },
            new() { Entity = "Đơn đăng ký", Event = "Bị từ chối", Trigger = "quản lý từ chối", Needed = true },
            new() { Entity = "Đơn đăng ký", Event = "Chờ duyệt", Needed = false }
        };

        var missing = NotificationMapBuilder.MissingRecipients(rows);

        // Dòng bỏ tích KHÔNG phải chỗ còn thiếu — nó là một quyết định.
        Assert.Equal(new[] { "Bị từ chối" }, missing.Select(r => r.Event));
        Assert.Equal("Đơn đăng ký — \"Bị từ chối\" (khi quản lý từ chối)",
            NotificationMapBuilder.EventLabel(missing[0]));
    }

    // "Không gửi" là một QUYẾT ĐỊNH và phải nói ra được: một sự kiện người dùng đã tắt mà khối này không
    // nhắc tới sẽ được bật lại ở bước soạn tài liệu.
    [Fact]
    public void ConfirmedBlock_NamesTheEventsTheUserTurnedOff()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Đã duyệt", Needed = true, To = { "Người tạo" }, Cc = { "HRBP" } },
            new NotificationMapRow { Entity = "Đơn đăng ký", Event = "Chờ duyệt", Needed = false }
        });

        var block = NotificationMapBuilder.RenderConfirmedBlock(json);

        Assert.Contains("To: Người tạo; CC: HRBP", block);
        Assert.Contains("KHÔNG gửi thông báo ở các sự kiện sau", block);
        Assert.Contains("Chờ duyệt", block);
        Assert.Contains("TUYỆT ĐỐI không tự suy rộng", block);
    }

    // Không có bảng (mọi dự án mới) ⇒ không khối nào.
    [Fact]
    public void ConfirmedBlock_IsNullWithoutATable()
    {
        Assert.Null(NotificationMapBuilder.RenderConfirmedBlock(null));
        Assert.Null(NotificationMapBuilder.RenderConfirmedBlock("[]"));
        Assert.False(NotificationMapBuilder.IsConfirmed("{ hỏng"));
    }
}
