namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// MỘT dòng của "bảng thông báo / nhắc nhở" mà các dự án CŨ đã chốt ở buổi phỏng vấn: một sự kiện, và ai
/// được gửi email khi nó xảy ra.
///
/// <para>
/// Bảng không còn được bày ra: sản phẩm của buổi phỏng vấn là một POC không gửi email nào, nên người nhận
/// email được ghi là CHƯA CHỐT cho đội Dev xác nhận sau POC
/// (<c>CoverageConfirmedTableGuard.NotificationDeferredNote</c>). Contract này chỉ còn là hình dạng để đọc
/// lại các cột <c>Project.NotificationMap</c> / <c>AgentConversation.NotificationMap</c> đã có dữ liệu —
/// xem <c>NotificationMapBuilder</c>.
/// </para>
///
/// <para>
/// Bất biến của một bảng đã lưu: mỗi dòng hoặc bỏ tích (<see cref="Needed"/> = false: không gửi), hoặc còn
/// tích kèm <see cref="To"/> không rỗng. Người nhận là tên người dùng tự viết ra hoặc một QUAN HỆ với bản
/// ghi ("Người tạo"), KHÔNG bao giờ được suy rộng thành "mọi người mang vai đó".
/// </para>
/// </summary>
public class NotificationMapRow
{
    /// <summary>Bản ghi chở sự kiện ("Leave Request") — rỗng với dòng NHẮC NHỞ không gắn vào bản ghi nào.</summary>
    public string Entity { get; set; } = "";

    /// <summary>Sự kiện làm thông báo được gửi — thường là tên trạng thái bản ghi vừa chuyển vào.</summary>
    public string Event { get; set; } = "";

    /// <summary>Khi nào sự kiện xảy ra, theo lời người dùng ("HOD bấm duyệt").</summary>
    public string Trigger { get; set; } = "";

    /// <summary>Người nhận CHÍNH.</summary>
    public List<string> To { get; set; } = new();

    /// <summary>Người nhận ĐỒNG GỬI; rỗng là ca thường gặp.</summary>
    public List<string> Cc { get; set; } = new();

    /// <summary>Sự kiện này CÓ gửi thông báo không — người dùng đã bỏ tích những sự kiện không cần báo.</summary>
    public bool Needed { get; set; } = true;

    /// <summary>Dòng có BẰNG CHỨNG trong hội thoại ⇒ đã bị khóa lúc bày bảng.</summary>
    public bool Locked { get; set; }

    /// <summary>Trích dẫn ngắn từ hội thoại — chỉ có nghĩa khi <see cref="Locked"/>.</summary>
    public string Evidence { get; set; } = "";

    /// <summary>Dòng do CHÍNH NGƯỜI DÙNG thêm (lời nhắc tự gõ), không phải dòng model ráp ra.</summary>
    public bool AddedByUser { get; set; }
}
