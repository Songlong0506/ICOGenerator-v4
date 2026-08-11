using System.ClientModel;
using System.Runtime.CompilerServices;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Nút "Test Connection" là chỗ người dùng đứng khi cấu hình model sai, nên câu nó nói ra quyết định người
// đó đi sửa cái gì trong nửa tiếng tiếp theo. Sự cố nguy hiểm nhất là proxy công ty chết: triệu chứng trông
// y hệt "endpoint không chạy", nên câu chung chung sẽ đẩy người ta đi soi endpoint và ApiKey của một endpoint
// hoàn toàn khỏe mạnh. Các test này khóa việc lỗi proxy phải được gọi đúng tên.
public class ModelConnectionTesterTests
{
    private const string ProxyAddress = "http://127.0.0.1:3128";

    private static AiModel Remote() => new()
    {
        ModelId = "gpt-5.6-luna",
        Endpoint = "https://api.openai.com/v1",
        ApiKey = "sk-test"
    };

    private static ModelConnectionTester TesterThatThrows(Exception failure, bool proxyEnabled = true) =>
        new(new ThrowingChatClientFactory(failure),
            new LlmSettings(proxyEnabled: proxyEnabled, proxyAddress: ProxyAddress));

    // Hình dạng THẬT của sự cố: SDK thử lại 4 lần rồi gói tất cả lại, còn lỗi proxy nằm dưới đáy — nếu chỉ
    // nhìn lớp ngoài thì không đời nào thấy chữ "proxy".
    private static Exception RetryWrapped(Exception cause) =>
        new ClientResultException("Retry failed after 4 tries.", response: null,
            innerException: new AggregateException(
                Enumerable.Range(0, 4).Select(_ => new HttpRequestException(
                    "An error occurred while sending the request.", cause)).ToArray()));

    private static HttpRequestException ProxyTunnelFailure() => new(
        HttpRequestError.ProxyTunnelError,
        $"The proxy tunnel request to proxy '{ProxyAddress}/' failed with status code '503'.");

    [Fact]
    public async Task TestAsync_WhenProxyRefusesTheTunnel_BlamesTheProxyNotTheEndpoint()
    {
        var outcome = await TesterThatThrows(RetryWrapped(ProxyTunnelFailure())).TestAsync(Remote());

        Assert.False(outcome.IsSuccess);
        Assert.Contains("Proxy", outcome.ErrorMessage);
        Assert.Contains(ProxyAddress, outcome.ErrorMessage);
        Assert.Contains("Llm:Proxy:Enabled", outcome.ErrorMessage);
        // Đây mới là điều quan trọng: KHÔNG được sai khiến người dùng đi kiểm tra endpoint.
        Assert.DoesNotContain("endpoint có đang chạy", outcome.ErrorMessage);
        // Nguyên văn của SDK vẫn phải xuống được dòng chi tiết để người sửa còn thấy mã 503.
        Assert.Contains("503", outcome.Detail);
    }

    // Proxy tắt hẳn (không ai nghe ở cổng 3128) chỉ để lại một lỗi kết nối trơ, không tự nhận diện được —
    // nhưng người đứng trước modal vẫn cần biết request này không đi thẳng.
    [Fact]
    public async Task TestAsync_WhenProxyEnabledForRemoteEndpoint_SaysTheCallGoesThroughIt()
    {
        var outcome = await TesterThatThrows(
            RetryWrapped(new HttpRequestException("Connection refused"))).TestAsync(Remote());

        Assert.False(outcome.IsSuccess);
        Assert.Contains("endpoint có đang chạy", outcome.ErrorMessage);
        Assert.Contains(ProxyAddress, outcome.ErrorMessage);
    }

    // Endpoint local đi thẳng (OpenAIChatClientFactory.IsLocalEndpoint) — nhắc tới proxy ở đây là chỉ sai chỗ.
    [Fact]
    public async Task TestAsync_ForLocalEndpoint_NeverMentionsTheProxy()
    {
        var local = new AiModel { ModelId = "qwen3", Endpoint = "http://127.0.0.1:1234/v1", ApiKey = "lm-studio" };

        var outcome = await TesterThatThrows(
            RetryWrapped(new HttpRequestException("Connection refused"))).TestAsync(local);

        Assert.False(outcome.IsSuccess);
        Assert.DoesNotContain("proxy", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAsync_WhenProxyDisabled_NeverMentionsTheProxy()
    {
        var outcome = await TesterThatThrows(
            RetryWrapped(new HttpRequestException("Connection refused")), proxyEnabled: false).TestAsync(Remote());

        Assert.False(outcome.IsSuccess);
        Assert.DoesNotContain("proxy", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // Cấu hình hỏng ngay từ khâu dựng client (endpoint không phải URL) phải hiện trong modal, không thành trang 500.
    [Fact]
    public async Task TestAsync_WhenClientCannotBeBuilt_ReportsTheReasonInsteadOfThrowing()
    {
        var outcome = await TesterThatThrows(new UriFormatException("Invalid URI: The format of the URI could not be determined."))
            .TestAsync(Remote());

        Assert.False(outcome.IsSuccess);
        Assert.Contains("Invalid URI", outcome.ErrorMessage);
    }

    private sealed class ThrowingChatClientFactory : IChatClientFactory
    {
        private readonly Exception _failure;

        public ThrowingChatClientFactory(Exception failure) => _failure = failure;

        public IChatClient Create(AiModel model) => new ThrowingChatClient(_failure);

        private sealed class ThrowingChatClient : IChatClient
        {
            private readonly Exception _failure;

            public ThrowingChatClient(Exception failure) => _failure = failure;

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                => throw _failure;

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                throw _failure;
#pragma warning disable CS0162 // Bắt buộc để compiler coi đây là iterator.
                yield break;
#pragma warning restore CS0162
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose() { }
        }
    }
}
