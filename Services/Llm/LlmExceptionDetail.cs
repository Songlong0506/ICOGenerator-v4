using System.ClientModel;
using System.Net.Sockets;
using System.Text;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Đọc ra NGUYÊN NHÂN THẬT của một lời gọi model hỏng, từ đống exception mà SDK gói lại.
///
/// Vì sao cần một chỗ riêng: lỗi mạng đi tới tay người dùng dưới dạng vô dụng nhất có thể —
/// <c>"Retry failed after 4 tries. (An error occurred while sending the request.) (…) (…) (…)"</c>. Cả bốn
/// vế đó chỉ là thông điệp CHUNG của <see cref="HttpRequestException"/>; thứ nói được chuyện gì đã xảy ra
/// (kết nối bị bên kia đóng, TLS không bắt tay được, DNS hỏng, proxy từ chối) nằm ở <c>InnerException</c>
/// mà không ai in ra. Không có nó thì mọi lỗi mạng trông giống hệt nhau và không thể chẩn đoán:
/// người dùng chỉ biết "AI thất bại", còn người sửa thì đoán mò.
///
/// <see cref="Describe"/> nối chuỗi nguyên nhân lại thành MỘT dòng đọc được, để nó lên thẳng bong bóng ⚠️
/// trong chat và cột lỗi của call log — hai chỗ người ta thực sự nhìn khi có sự cố.
/// </summary>
internal static class LlmExceptionDetail
{
    private const int MaxLength = 600;

    /// <summary>
    /// Một dòng "nguyên nhân nối nguyên nhân": thông điệp lớp ngoài rồi tới từng lớp trong, bỏ các lớp lặp
    /// lại chính nó. Chính sách retry gói 4 lần thử giống hệt nhau vào một <see cref="AggregateException"/>
    /// nên chỉ đi theo NHÁNH ĐẦU — bốn bản sao của cùng một lỗi không nói thêm được gì.
    /// </summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = (Exception?)ex;

        while (current != null && parts.Count < 5)
        {
            var message = HeadMessageOf(current);
            if (!string.IsNullOrEmpty(message) && seen.Add(message))
            {
                // Tên kiểu đi kèm từ lớp thứ hai trở đi: "An existing connection was forcibly closed" mà
                // không biết đó là SocketException hay IOException thì vẫn còn mơ hồ.
                parts.Add(parts.Count == 0 ? message : $"{current.GetType().Name}: {message}");
            }

            current = current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0
                ? aggregate.Flatten().InnerExceptions[0]
                : current.InnerException;
        }

        var text = string.Join(" → ", parts);
        return text.Length <= MaxLength ? text : text[..MaxLength] + "…";
    }

    /// <summary>
    /// Thông điệp RIÊNG của một lớp exception. Với <see cref="AggregateException"/> phải cắt bỏ phần đuôi,
    /// vì .NET tự dán nguyên văn từng exception con vào <c>Message</c> theo dạng
    /// <c>"&lt;câu gốc&gt; (con 1) (con 2) …"</c> — chính là thứ biến một lỗi mạng thành bốn dòng y hệt nhau
    /// trên màn hình. Các lớp con vẫn được in, nhưng ở dạng có tên kiểu và không lặp. Cắt bằng cách so
    /// ĐÚNG chuỗi của các exception con (từ cuối lên) chứ không dò dấu ngoặc: câu gốc hoàn toàn có thể có
    /// dấu ngoặc của riêng nó.
    /// </summary>
    private static string HeadMessageOf(Exception ex)
    {
        var text = (ex.Message ?? string.Empty).Trim();
        if (ex is not AggregateException aggregate)
            return text;

        foreach (var inner in aggregate.InnerExceptions.Reverse())
        {
            var suffix = $" ({inner.Message})";
            if (text.EndsWith(suffix, StringComparison.Ordinal))
                text = text[..^suffix.Length];
        }
        return text;
    }

    /// <summary>
    /// Nguyên nhân đầu tiên trong chuỗi mà app biết cách diễn giải. Lỗi mạng thường bị SDK gói lại sau vài
    /// lần retry nên phải đi xuống; dừng ngay khi gặp một loại đã biết.
    /// </summary>
    public static Exception Unwrap(Exception ex)
    {
        while (true)
        {
            if (ex is ClientResultException { Status: > 0 } or OperationCanceledException
                or HttpRequestException or SocketException)
                return ex;

            var inner = ex is AggregateException aggregate && aggregate.InnerExceptions.Count > 0
                ? aggregate.Flatten().InnerExceptions[0]
                : ex.InnerException;
            if (inner is null)
                return ex;

            ex = inner;
        }
    }

    /// <summary>
    /// Lời gọi chết ở tầng TRUYỀN TẢI (request không tới được endpoint) chứ không phải endpoint trả lỗi.
    /// Nhận theo HỌ exception chứ không so chuỗi — thông điệp thay đổi theo hệ điều hành và theo lớp nào
    /// bắt được lỗi trước, còn kiểu thì không. Phải đi hết cây vì chính sách retry của System.ClientModel
    /// gói các lần thử vào một <see cref="AggregateException"/>.
    /// </summary>
    public static bool IsTransportFailure(Exception ex) => ex switch
    {
        HttpRequestException or SocketException or IOException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsTransportFailure),
        _ => ex.InnerException is { } inner && IsTransportFailure(inner),
    };
}
