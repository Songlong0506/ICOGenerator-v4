namespace ICOGenerator.Services.Diagnostics;

/// <summary>
/// Kết luận của một dòng kiểm tra môi trường. KHÔNG lưu DB — chỉ đi ra JSON cho nút "Test Environment" ở
/// trang Settings, nên đổi tên giá trị không ảnh hưởng dữ liệu nào.
/// </summary>
public enum EnvironmentCheckStatus
{
    /// <summary>Đạt — tính năng phụ thuộc vào thứ này chạy được.</summary>
    Pass,

    /// <summary>Chạy được nhưng thiếu/lệch một phần (tính năng fail-open bị tắt, cấu hình dễ gây lỗi về sau).</summary>
    Warning,

    /// <summary>Hỏng — tính năng phụ thuộc vào thứ này sẽ không chạy.</summary>
    Fail,

    /// <summary>Thông tin để đối chiếu khi debug (tài khoản chạy app, đường dẫn…), không phải kết luận.</summary>
    Info
}
