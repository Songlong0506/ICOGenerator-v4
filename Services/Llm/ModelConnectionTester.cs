using System.ClientModel;
using System.Diagnostics;
using System.Net.Sockets;
using ICOGenerator.Domain;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// <see cref="IModelConnectionTester"/> qua đúng đường dây thật của app: cùng <see cref="IChatClientFactory"/>
/// (nên cùng lựa chọn proxy theo endpoint và cùng shim tương thích request) mà agent/BA đang dùng, chỉ khác là
/// không có middleware log/budget — một lời gọi thử không nên xuất hiện trong call log hay bị tính tiền.
/// Deadline riêng và ngắn (<c>Llm:TestConnectionTimeoutSeconds</c>) vì người dùng đang đứng chờ trước modal,
/// khác hẳn deadline 600s của một lượt agent.
/// </summary>
public sealed class ModelConnectionTester : IModelConnectionTester
{
    private const int DefaultTimeoutSeconds = 30;
    // Chỉ cần biết endpoint có trả lời hay không, nên xin đúng vài token: nhanh và gần như không tốn tiền.
    private const int MaxOutputTokens = 16;
    private const int DetailMaxLength = 400;
    private const string ProbePrompt = "ping";

    private readonly IChatClientFactory _chatClientFactory;
    private readonly int _timeoutSeconds;

    public ModelConnectionTester(IChatClientFactory chatClientFactory, IConfiguration configuration)
    {
        _chatClientFactory = chatClientFactory;
        _timeoutSeconds = configuration.GetValue("Llm:TestConnectionTimeoutSeconds", DefaultTimeoutSeconds);
    }

    public async Task<ModelConnectionTestOutcome> TestAsync(AiModel model, CancellationToken cancellationToken = default)
    {
        var options = new ChatOptions { MaxOutputTokens = MaxOutputTokens };
        var messages = new List<ChatMessage> { new(ChatRole.User, ProbePrompt) };

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Create() nằm TRONG try vì nó cũng có thể ném với cấu hình xấu (endpoint không phải URL hợp lệ,
            // ApiKey rỗng) — người dùng cần thấy lỗi đó trong modal chứ không phải một trang 500.
            var client = _chatClientFactory.Create(model);
            var response = await client.GetResponseAsync(messages, options, linkedCts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return new ModelConnectionTestOutcome(true, stopwatch.ElapsedMilliseconds, 200, Truncate(response.Text));
        }
        // Người dùng đóng tab / app shutdown: không phải lỗi cấu hình model, để caller xử lý như hủy bình thường.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var (status, message, detail) = Describe(ex);
            return new ModelConnectionTestOutcome(false, stopwatch.ElapsedMilliseconds, status, detail, message);
        }
    }

    // Cùng cách phân loại lỗi như ModelCallLoggingChatClient: non-2xx từ API mang theo status code, deadline
    // của chính mình là timeout, còn lại là lỗi mạng/DNS/URL. Người dùng đang sửa cấu hình nên câu chính phải
    // nói được "sai ở đâu"; nguyên văn của SDK đẩy xuống dòng chi tiết.
    private (int? Status, string Message, string? Detail) Describe(Exception ex)
    {
        var cause = Unwrap(ex);
        return cause switch
        {
            // Status 0 = SDK không nhận được HTTP response nào (lỗi transport), không phải lỗi do API trả về.
            ClientResultException { Status: > 0 } api => (api.Status, DescribeStatus(api.Status), Truncate(api.Message)),
            OperationCanceledException => (null, $"Không có phản hồi trong {_timeoutSeconds}s (timeout).", null),
            HttpRequestException or SocketException => (null,
                "Không kết nối được tới endpoint — kiểm tra địa chỉ/port và xem endpoint có đang chạy.",
                Truncate(cause.Message)),
            _ => (null, cause.Message, ReferenceEquals(cause, ex) ? null : Truncate(ex.Message))
        };
    }

    // Lỗi mạng thường bị SDK gói lại sau vài lần retry ("Retry failed after 4 tries. (…) (…)"), nên đi xuống
    // tới nguyên nhân thật; dừng ngay khi gặp một loại mình biết cách diễn giải.
    private static Exception Unwrap(Exception ex)
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

    private static string DescribeStatus(int status) => status switch
    {
        401 or 403 => $"API trả {status} — ApiKey sai hoặc không có quyền.",
        404 => "API trả 404 — kiểm tra lại Endpoint (thường phải có hậu tố /v1) và Model ID.",
        429 => "API trả 429 — bị giới hạn tần suất (rate limit), thử lại sau.",
        _ => $"API trả lỗi {status}."
    };

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();
        return text.Length <= DetailMaxLength ? text : text[..DetailMaxLength] + "…";
    }
}
