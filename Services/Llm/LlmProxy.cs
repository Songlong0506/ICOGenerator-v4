using System.Net;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Dựng <see cref="IWebProxy"/> cho HttpClient "proxied" từ <see cref="LlmSettings"/> — chỗ DUY NHẤT biến
/// cấu hình <c>Llm:Proxy</c> thành một đối tượng proxy thật.
///
/// Vì sao không nằm luôn trong file đăng ký DI: hai chuyện ở đây có thể sai âm thầm và chỉ lộ ra dưới dạng
/// "lời gọi LLM chết" trên máy người khác — (1) proxy công ty đòi <c>407</c> mà app không biết trả lời, và
/// (2) endpoint nội bộ bị đẩy qua proxy công ty rồi bị chính nó từ chối. Cả hai cần test, mà một dòng
/// khởi tạo nằm giữa <c>AddHttpClient</c> thì không test được.
/// </summary>
internal static class LlmProxy
{
    /// <summary>
    /// <c>null</c> khi tắt proxy — handler sẽ đi thẳng. Credential chỉ gắn khi được khai tường minh
    /// (<c>Llm:Proxy:UseDefaultCredentials</c>): với relay local kiểu px/CNTLM thì chính relay lo phần
    /// xác thực, app gửi thêm danh tính Windows là thừa.
    /// </summary>
    public static IWebProxy? Create(LlmSettings settings)
    {
        if (!settings.ProxyEnabled)
            return null;

        var proxy = new WebProxy(settings.ProxyAddress)
        {
            // Endpoint localhost vốn đã đi qua HttpClient "direct" nên cờ này gần như không bao giờ được
            // dùng tới; bật cho đúng ngữ nghĩa "địa chỉ trong cùng máy thì không việc gì phải qua proxy".
            BypassProxyOnLocal = true,
            UseDefaultCredentials = settings.ProxyUseDefaultCredentials
        };

        var patterns = settings.ProxyBypassList.Select(ToRegex).ToArray();
        if (patterns.Length > 0)
            proxy.BypassList = patterns;

        return proxy;
    }

    /// <summary>
    /// <see cref="WebProxy.BypassList"/> nhận REGEX, nhưng người viết appsettings nghĩ theo kiểu
    /// <c>no_proxy</c> — họ gõ <c>.bosch.com</c> và mong nó khớp mọi host con. Để nguyên chuỗi đó làm regex
    /// thì dấu chấm biến thành "ký tự bất kỳ" và <c>xbosch.com</c> cũng lọt, còn một mục lỡ chứa <c>*</c>
    /// sẽ ném <see cref="ArgumentException"/> ngay lúc khởi động. Nên ở đây escape hết rồi tự dựng lấy
    /// regex neo hai đầu.
    /// </summary>
    private static string ToRegex(string entry)
    {
        // ".bosch.com" và "*.bosch.com" cùng nghĩa: chính tên miền đó và mọi host con của nó.
        var host = entry.TrimStart('*').TrimStart('.');
        var suffixOnly = entry.StartsWith('.') || entry.StartsWith('*');
        var hostPattern = suffixOnly
            ? $"(?:.*\\.)?{Regex.Escape(host)}"
            : Regex.Escape(host);

        // Chuỗi mà WebProxy đem so là URI đích ở dạng "scheme://host:port".
        return $"^[a-z][a-z0-9+.-]*://{hostPattern}(?::[0-9]+)?$";
    }
}
