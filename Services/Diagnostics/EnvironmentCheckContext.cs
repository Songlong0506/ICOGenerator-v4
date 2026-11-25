namespace ICOGenerator.Services.Diagnostics;

/// <summary>
/// Những gì một lượt kiểm tra cần biết về REQUEST đang bấm nút — thứ mà Services không tự đọc được (không
/// phụ thuộc HttpContext). Hiện chỉ có HTTPS: cookie đăng nhập ở môi trường khác Development là Secure-only.
/// </summary>
public sealed record EnvironmentCheckContext(bool RequestIsHttps);
