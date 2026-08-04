using System.Diagnostics;
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
    private const string ProbePrompt = "ping";

    private readonly IChatClientFactory _chatClientFactory;
    private readonly int _timeoutSeconds;

    public ModelConnectionTester(IChatClientFactory chatClientFactory, LlmSettings settings)
    {
        _chatClientFactory = chatClientFactory;
        _timeoutSeconds = settings.TestConnectionTimeoutSeconds;
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

    // Phân loại lỗi dùng CHUNG với mọi lượt gọi thật (xem LlmFailureDescriber): non-2xx từ API mang theo
    // status code, deadline của chính mình là timeout, còn lại là lỗi mạng/DNS/URL. Người dùng đang sửa cấu
    // hình nên câu chính phải nói được "sai ở đâu"; nguyên văn của SDK đẩy xuống dòng chi tiết.
    private (int? Status, string Message, string? Detail) Describe(Exception ex)
    {
        var failure = LlmFailureDescriber.Describe(ex, _timeoutSeconds);
        return (failure.Status, failure.Message, failure.Detail);
    }

    private static string? Truncate(string? text) => LlmFailureDescriber.Truncate(text);
}
