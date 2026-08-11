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
    // Chỉ cần biết endpoint có trả lời hay không, nên xin đúng vài token: nhanh và gần như không tốn tiền.
    private const int MaxOutputTokens = 16;
    private const int DetailMaxLength = 400;
    private const string ProbePrompt = "ping";

    private readonly IChatClientFactory _chatClientFactory;
    private readonly int _timeoutSeconds;
    private readonly bool _proxyEnabled;
    private readonly string _proxyAddress;

    public ModelConnectionTester(IChatClientFactory chatClientFactory, LlmSettings settings)
    {
        _chatClientFactory = chatClientFactory;
        _timeoutSeconds = settings.TestConnectionTimeoutSeconds;
        _proxyEnabled = settings.ProxyEnabled;
        _proxyAddress = settings.ProxyAddress;
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
            var (status, message, detail) = Describe(ex, model);
            return new ModelConnectionTestOutcome(false, stopwatch.ElapsedMilliseconds, status, detail, message);
        }
    }

    // Cùng cách phân loại lỗi như ModelCallLoggingChatClient: non-2xx từ API mang theo status code, deadline
    // của chính mình là timeout, còn lại là lỗi mạng/DNS/URL. Người dùng đang sửa cấu hình nên câu chính phải
    // nói được "sai ở đâu"; nguyên văn của SDK đẩy xuống dòng chi tiết.
    private (int? Status, string Message, string? Detail) Describe(Exception ex, AiModel model)
    {
        // Hỏi trước cả Unwrap: lỗi proxy nằm DƯỚI một HttpRequestException chung chung, mà Unwrap thì dừng
        // ở lớp đầu tiên nó nhận ra — cứ để nó chạy trước thì câu trả lời luôn là "endpoint không chạy".
        if (LlmExceptionDetail.IsProxyFailure(ex))
            return (null,
                $"Proxy {_proxyAddress} không mở được đường ra endpoint — endpoint có thể vẫn khỏe. "
                + "Kiểm tra proxy có đang chạy không, hoặc đặt Llm:Proxy:Enabled=false nếu máy này ra thẳng Internet.",
                Truncate(LlmExceptionDetail.Describe(ex)));

        var cause = LlmExceptionDetail.Unwrap(ex);
        return cause switch
        {
            // Status 0 = SDK không nhận được HTTP response nào (lỗi transport), không phải lỗi do API trả về.
            ClientResultException { Status: > 0 } api => (api.Status, DescribeStatus(api.Status), Truncate(api.Message)),
            OperationCanceledException => (null, $"Không có phản hồi trong {_timeoutSeconds}s (timeout).", null),
            HttpRequestException or SocketException => (null,
                "Không kết nối được tới endpoint — kiểm tra địa chỉ/port và xem endpoint có đang chạy."
                    + ProxyNote(model),
                // Cả chuỗi nguyên nhân, không chỉ câu chung chung của lớp ngoài — xem LlmExceptionDetail.
                Truncate(LlmExceptionDetail.Describe(ex))),
            _ => (null, cause.Message, ReferenceEquals(cause, ex) ? null : Truncate(ex.Message))
        };
    }

    /// <summary>
    /// Lưới đỡ cho các lỗi proxy KHÔNG tự nhận diện được (proxy tắt hẳn thì chỉ còn một lỗi kết nối trơ,
    /// không nói được nó đang nối tới proxy hay tới endpoint). Không khẳng định proxy là thủ phạm — chỉ nói
    /// ra sự thật mà người đứng trước modal không có cách nào biết: request này không đi thẳng.
    /// </summary>
    private string ProxyNote(AiModel model) =>
        _proxyEnabled && !OpenAIChatClientFactory.IsLocalEndpoint(model.Endpoint)
            ? $" Lời gọi này đi qua proxy {_proxyAddress} (Llm:Proxy), nên proxy chết cũng cho ra lỗi y hệt."
            : string.Empty;

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
