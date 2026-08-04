using System.ClientModel;
using System.Net.Sockets;

namespace ICOGenerator.Services.Llm;

/// <summary>Phân loại thô một lời gọi model thất bại — đủ để chọn câu báo lỗi và quyết định có thử lại hay không.</summary>
public enum LlmFailureKind
{
    /// <summary>Chưa phân loại được (lỗi lập trình, lỗi SDK lạ…) — chỉ còn cách hiện nguyên văn.</summary>
    Unknown = 0,

    /// <summary>Endpoint CÓ trả lời nhưng non-2xx (400 tham số sai, 401 key, 429 rate limit…).</summary>
    Api,

    /// <summary>Deadline của chính app (<see cref="LlmSettings.RequestTimeoutSeconds"/>) hết trước khi có phản hồi.</summary>
    Timeout,

    /// <summary>
    /// KHÔNG nhận được HTTP response nào: DNS/TCP/TLS/proxy, hoặc phía kia đóng kết nối giữa lúc app đang
    /// ghi body (rất hay gặp khi request quá lớn — vd một lượt kèm cả chục ảnh base64).
    /// </summary>
    Transport,
}

/// <summary>Kết quả phân loại: câu chính (nói "sai ở đâu") + nguyên văn của SDK để ở dòng chi tiết.</summary>
public sealed record LlmFailure(LlmFailureKind Kind, int? Status, string Message, string? Detail);

/// <summary>
/// Nguồn sự thật DUY NHẤT cho việc "exception này nghĩa là gì" trên mọi đường gọi model: modal Test
/// Connection (<see cref="ModelConnectionTester"/>) và mọi lượt gọi thật đi qua
/// <see cref="ModelCallLoggingChatClient"/> — bong bóng ⚠️ trong chat BA lấy chữ từ đó.
///
/// Lý do phải bóc tách: SDK OpenAI gói lỗi mạng lại sau khi tự thử lại, nên thứ nổi lên trên cùng là
/// <c>"Retry failed after 4 tries. (An error occurred while sending the request.) (…) (…) (…)"</c> —
/// một câu không nói được gì về nguyên nhân, và trước đây nó được chép thẳng vào chat khiến người dùng
/// không biết là hỏng mạng, hỏng TLS hay request quá lớn. <see cref="Unwrap"/> đi xuống tới exception
/// thật, <see cref="Describe"/> gọi tên nó bằng tiếng người.
/// </summary>
public static class LlmFailureDescriber
{
    private const int DetailMaxLength = 400;

    public static LlmFailure Describe(Exception ex, int timeoutSeconds)
    {
        var cause = Unwrap(ex);
        return cause switch
        {
            // Status 0 = SDK không nhận được HTTP response nào (lỗi transport), không phải lỗi do API trả về.
            ClientResultException { Status: > 0 } api =>
                new LlmFailure(LlmFailureKind.Api, api.Status, DescribeStatus(api.Status), Truncate(api.Message)),
            OperationCanceledException =>
                new LlmFailure(LlmFailureKind.Timeout, null, $"Không có phản hồi trong {timeoutSeconds}s (timeout).", null),
            HttpRequestException or SocketException =>
                new LlmFailure(LlmFailureKind.Transport, null,
                    "Không kết nối được tới endpoint — kiểm tra địa chỉ/port và xem endpoint có đang chạy.",
                    Truncate(cause.Message)),
            _ => new LlmFailure(LlmFailureKind.Unknown, null, cause.Message,
                ReferenceEquals(cause, ex) ? null : Truncate(ex.Message)),
        };
    }

    /// <summary>
    /// Lỗi mạng thường bị SDK gói lại sau vài lần retry ("Retry failed after 4 tries. (…) (…)"), nên đi xuống
    /// tới nguyên nhân thật; dừng ngay khi gặp một loại mình biết cách diễn giải.
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

    public static string DescribeStatus(int status) => status switch
    {
        401 or 403 => $"API trả {status} — ApiKey sai hoặc không có quyền.",
        404 => "API trả 404 — kiểm tra lại Endpoint (thường phải có hậu tố /v1) và Model ID.",
        413 => "API trả 413 — request quá lớn (thường do gửi kèm quá nhiều ảnh tài liệu nguồn).",
        429 => "API trả 429 — bị giới hạn tần suất (rate limit), thử lại sau.",
        _ => $"API trả lỗi {status}."
    };

    public static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();
        return text.Length <= DetailMaxLength ? text : text[..DetailMaxLength] + "…";
    }
}
