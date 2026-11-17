using ICOGenerator.Localization;
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
    // Chỉ cần biết endpoint có trả lời hay không, nên xin ít token: nhanh và gần như không tốn tiền. Không để
    // quá thấp (từng là 16): model reasoning tiêu hết số đó vào suy nghĩ và OpenAI trả 400 "max_tokens or model
    // output limit was reached" — modal báo thất bại cho một cấu hình đang đúng.
    private const int MaxOutputTokens = 100;
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
                Loc.T("ModelConnectionTester.Describe", _proxyAddress),
                Truncate(LlmExceptionDetail.Describe(ex)));

        // Model claude-code://… không đi qua HttpClient nên Llm:Proxy (và gợi ý UseDefaultCredentials ở trên) vô
        // nghĩa với nó: CLI tự ra mạng theo biến môi trường. Lỗi proxy của CLI chỉ tới đây dưới dạng chữ, nên
        // nhận bằng chữ rồi chỉ đúng chỗ sửa là Llm:ClaudeCode:Environment.
        if (ClaudeCodeCli.IsClaudeCodeEndpoint(model.Endpoint)
            && ex.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase))
            return (null, Loc.T("ModelConnectionTester.Describe.ClaudeCodeProxy"), Truncate(ex.Message));

        var cause = LlmExceptionDetail.Unwrap(ex);
        return cause switch
        {
            // Status 0 = SDK không nhận được HTTP response nào (lỗi transport), không phải lỗi do API trả về.
            ClientResultException { Status: > 0 } api => (api.Status, DescribeStatus(api.Status), Truncate(api.Message)),
            OperationCanceledException => (null, Loc.T("ModelConnectionTester.Describe.2", _timeoutSeconds), null),
            HttpRequestException or SocketException => (null,
                Loc.T("ModelConnectionTester.Describe.3", ProxyNote(model)),
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
            ? Loc.T("ModelConnectionTester.ProxyNote", _proxyAddress)
            : string.Empty;

    private static string DescribeStatus(int status) => status switch
    {
        401 or 403 => Loc.T("ModelConnectionTester.DescribeStatus", status),
        404 => Loc.T("ModelConnectionTester.DescribeStatus.2"),
        429 => Loc.T("ModelConnectionTester.DescribeStatus.3"),
        _ => Loc.T("ModelConnectionTester.DescribeStatus.4", status)
    };

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();
        return text.Length <= DetailMaxLength ? text : text[..DetailMaxLength] + "…";
    }
}
