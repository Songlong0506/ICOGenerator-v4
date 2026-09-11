namespace ICOGenerator.Services.Browser;

/// <summary>
/// Chốt chặn URL của WebPilot — hàm thuần để test được mà không cần browser.
///
/// <para>
/// Cố ý KHÔNG có allowlist domain và KHÔNG chặn dải IP nội bộ: một trong hai kịch bản đích của
/// WebPilot là điền form trên trang nội bộ của công ty, chặn mạng nội bộ là chặn đúng việc người dùng
/// muốn làm. Rào chắn vì vậy nằm ở chỗ khác — vai WebPilot KHÔNG được cấp tool chạy lệnh, tool git hay
/// tool file (xem <c>docs/agents-and-tools.md</c>), nên thiệt hại xấu nhất của một trang độc vẫn nằm
/// trong đúng cái trình duyệt đó.
/// </para>
///
/// <para>
/// Cái PHẢI chặn là scheme: <c>file:</c> biến trình duyệt thành đường vòng đọc đĩa, qua mặt
/// <c>AllowedFileExtensions</c> mà mọi tool file đều đi qua; <c>javascript:</c>/<c>data:</c> là đường
/// chạy mã tuỳ ý ngay trong trang đang mở.
/// </para>
/// </summary>
public static class WebUrlPolicy
{
    /// <summary>Endpoint metadata của cloud — không phải "mạng nội bộ của công ty" mà là chìa khoá máy chủ.</summary>
    private static readonly string[] BlockedHosts = ["169.254.169.254", "metadata.google.internal"];

    /// <summary>
    /// Chuẩn hoá và kiểm tra một URL model đưa xuống. Trả về URL đã chuẩn hoá, hoặc lý do từ chối
    /// (tiếng Việt, để model đọc được và tự sửa ở lượt sau).
    /// </summary>
    public static (string? Url, string? Error) Validate(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
            return (null, "Thiếu url.");

        // Người dùng (và model) hay gõ "example.com" — đoán https là đoán đúng gần như mọi lần, và
        // vẫn đi qua đủ các chốt bên dưới.
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return (null, $"URL không hợp lệ: {raw}");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return (null, $"Chỉ mở được http/https — bị từ chối: {uri.Scheme}:");

        // https://user:pass@host — credential nhúng trong URL sẽ nằm nguyên văn trong log lời gọi tool.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return (null, "URL không được nhúng tài khoản/mật khẩu (dạng https://user:pass@host).");

        if (BlockedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            return (null, $"Host bị chặn: {uri.Host}");

        return (uri.ToString(), null);
    }
}
