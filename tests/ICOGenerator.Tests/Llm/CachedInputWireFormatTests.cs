using System.Net;
using System.Text;
using ICOGenerator.Domain;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Llm;

/// <summary>
/// Số token cache đi từ dây vào <c>UsageDetails.CachedInputTokenCount</c> qua HAI lớp mà repo này không sở
/// hữu (SDK OpenAI đọc <c>prompt_tokens_details.cached_tokens</c>, rồi Microsoft.Extensions.AI.OpenAI ánh xạ
/// sang property). Không có gì trong code ta buộc chặng đó phải xảy ra: nâng phiên bản gói mà ánh xạ đổi thì
/// <c>CachedPromptTokens</c> im lặng về 0 — không lỗi nào nổ ra, chi phí vẫn ra một con số hợp lý, chỉ là con
/// số ĐẮT hơn hóa đơn thật.
/// Các test khác stub thẳng <see cref="IChatClient"/> nên chúng chỉ pin nhánh code của mình và sẽ vẫn xanh
/// nguyên trong tình huống đó. Ở đây SDK OpenAI THẬT đọc một body <c>usage</c> đúng hình dạng OpenAI trả về.
/// </summary>
public class CachedInputWireFormatTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private string _endpoint = null!;
    private Task _serverLoop = null!;

    public Task InitializeAsync()
    {
        var port = FreePort();
        _endpoint = $"http://127.0.0.1:{port}/v1";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _serverLoop = Task.Run(ServeAsync);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _listener.Stop();
        return Task.CompletedTask;
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // Khối usage đúng như OpenAI trả về khi prompt trúng cache: cached_tokens là một phần CON của
    // prompt_tokens (1000 = 768 cache + 232 phải trả giá đầy đủ).
    private const string UsageJson =
        "\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":40,\"total_tokens\":1040,\"prompt_tokens_details\":{\"cached_tokens\":768}}";

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            // Đường chat thường của app là STREAMING; OpenAI gửi usage ở chunk cuối (stream_options.include_usage),
            // chunk đó có choices rỗng. Trả đúng hình dạng ấy để phép đo không phụ thuộc vào stub dễ dãi.
            await WriteAsync(context, "text/event-stream",
                "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"stub\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":null}]}\n\n"
                + "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"stub\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
                + "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"stub\",\"choices\":[]," + UsageJson + "}\n\n"
                + "data: [DONE]\n\n");
            _ = body;
        }
    }

    private static async Task WriteAsync(HttpListenerContext context, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private LlmClient Client()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(OpenAIChatClientFactory.DirectClientName)
            .AddHttpMessageHandler(() => new LlmRequestCompatibilityHandler());
        var httpClientFactory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        return new LlmClient(
            new OpenAIChatClientFactory(httpClientFactory),
            new NullModelCallLogger(),
            new NoBudgetLimit(),
            new LlmSettings(),
            NullLogger<LlmClient>.Instance);
    }

    [Fact]
    public async Task CachedTokens_FromARealOpenAiUsageBody_ReachTheCallResult()
    {
        var model = new AiModel { ModelId = "stub-model", Endpoint = _endpoint, ApiKey = "sk-stub" };

        var result = await Client().ChatWithLogAsync(
            model,
            new List<ChatMessage> { new(ChatRole.User, "xin chào") },
            0.3,
            new ModelCallLogContext(Guid.NewGuid(), new Agent(), "CachedInputWireFormatTest"));

        Assert.True(result.IsSuccess);
        Assert.Equal(1000, result.PromptTokens);
        Assert.Equal(768, result.CachedPromptTokens);
        Assert.Equal(40, result.CompletionTokens);
    }

    // Cùng một lượt gọi, chỉ khác đơn giá: phần cache rẻ hơn 10 lần thì hóa đơn phải rẻ theo, chứ không phải
    // "vẫn tính như cũ". Đây là toàn bộ lý do đo cached token ngay từ đầu.
    [Fact]
    public async Task CachedTokens_MakeTheCallCheaper_ThanChargingEveryPromptTokenAtFullPrice()
    {
        var model = new AiModel { ModelId = "stub-model", Endpoint = _endpoint, ApiKey = "sk-stub" };

        var result = await Client().ChatWithLogAsync(
            model,
            new List<ChatMessage> { new(ChatRole.User, "xin chào") },
            0.3,
            new ModelCallLogContext(Guid.NewGuid(), new Agent(), "CachedInputWireFormatTest"));

        var price = new LlmPrice(Input: 2m, CachedInput: 0.2m, Output: 10m);
        var actual = LlmCost.Usd(result.PromptTokens, result.CachedPromptTokens, result.CompletionTokens, price);
        var ignoringCache = LlmCost.Usd(result.PromptTokens, 0, result.CompletionTokens, price);

        Assert.True(actual < ignoringCache);
        // 232 @ $2/1M ($0.000464) + 768 @ $0.2/1M ($0.0001536) + 40 @ $10/1M ($0.0004)
        Assert.Equal(0.0010176m, actual);
    }

    private sealed class NullModelCallLogger : IModelCallLogger
    {
        public Task LogAsync(Guid projectId, Agent agent, LlmCallResult callResult, int step, string purpose, Guid? workflowRunId = null)
            => Task.CompletedTask;
    }

    private sealed class NoBudgetLimit : IBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
