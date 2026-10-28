using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ICOGenerator.Domain;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Evals;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using Xunit;

namespace ICOGenerator.Tests.Llm;

/// <summary>
/// Gateway trước endpoint cắt lời gọi chạy lâu ("The response ended prematurely") — đi qua SDK THẬT, trên một
/// server TCP giả đóng socket đúng lúc cần, vì cả hai thứ được khóa ở đây (SDK thử lại bao nhiêu lần, exception
/// trông ra sao trước/sau header) đều là hành vi của SDK + HttpClient chứ không phải của code mình.
/// </summary>
public class ResponseCutOffTests
{
    private enum Cut { BeforeHeaders, MidStream }

    /// <summary>Server giả: đọc request, chờ <paramref name="delay"/>, rồi đóng kết nối theo kiểu <paramref name="cut"/>.</summary>
    private sealed class CuttingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private int _connections;

        public CuttingServer(Cut cut, TimeSpan delay)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); }
                    catch { return; }

                    Interlocked.Increment(ref _connections);
                    _ = Task.Run(() => ServeAsync(client, cut, delay));
                }
            });
        }

        public string Endpoint => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
        public int Connections => Volatile.Read(ref _connections);

        private static async Task ServeAsync(TcpClient client, Cut cut, TimeSpan delay)
        {
            using (client)
            {
                var stream = client.GetStream();
                try { await stream.ReadAsync(new byte[65536]); } catch { return; }

                if (cut == Cut.MidStream)
                {
                    const string data = "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\","
                        + "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"x\"},\"finish_reason\":null}]}\n\n";
                    var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n"
                        + $"{Encoding.UTF8.GetByteCount(data):X}\r\n{data}\r\n";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
                    await stream.FlushAsync();
                }

                await Task.Delay(delay);
            }
        }

        public void Dispose() => _listener.Stop();
    }

    private static LlmClient Client()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(OpenAIChatClientFactory.DirectClientName);
        var httpClientFactory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new LlmClient(new OpenAIChatClientFactory(httpClientFactory), new NullModelCallLogger(),
            new NoBudgetLimit(), new LlmSettings(), NullLogger<LlmClient>.Instance);
    }

    private static AiModel Model(string endpoint) => new() { ModelId = "m", Endpoint = endpoint, ApiKey = "k" };

    private static Task<LlmCallResult> CallAsync(string endpoint) =>
        Client().ChatWithLogAsync(Model(endpoint), new() { new(ChatRole.User, "hi") },
            new ModelCallLogContext(Guid.NewGuid(), new Agent(), "CutOffTest"));

    // Bị cắt NGAY (kết nối keep-alive cũ trong pool) thì thử lại là đúng — kết nối mới gần như chắc chắn qua.
    [Fact]
    public async Task AFastCutOff_IsStillRetried()
    {
        using var server = new CuttingServer(Cut.BeforeHeaders, TimeSpan.Zero);

        var result = await CallAsync(server.Endpoint);

        Assert.False(result.IsSuccess);
        Assert.Equal(4, server.Connections);
    }

    // Bị cắt SAU một lúc chờ là gateway chạm ngưỡng timeout CỐ ĐỊNH của nó: thử lại chỉ chờ y hệt rồi bị cắt
    // y hệt — bốn lần là ~140s người dùng ngồi nhìn cho một lỗi ~35s.
    [Fact]
    public async Task ASlowCutOff_IsNotRetried()
    {
        using var server = new CuttingServer(Cut.BeforeHeaders, TimeSpan.FromMilliseconds(600));
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(server.Endpoint),
            RetryPolicy = new LlmRetryPolicy(slowCutOff: TimeSpan.FromMilliseconds(300)),
        };
        var chat = new OpenAIClient(new ApiKeyCredential("k"), options).GetChatClient("m").AsIChatClient();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in chat.GetStreamingResponseAsync("hi")) { }
        });

        Assert.Equal(1, server.Connections);
    }

    // Không có nổi header dù lời gọi đi dạng stream ⇒ gateway giữ cả phản hồi. Gợi ý "hạ Structured Output"
    // ở ca này là chỉ sai chỗ — người dùng làm theo, lỗi vẫn y nguyên.
    [Fact]
    public async Task ACutBeforeHeaders_IsExplainedAsTheGatewayHoldingTheWholeResponse()
    {
        using var server = new CuttingServer(Cut.BeforeHeaders, TimeSpan.Zero);

        var result = await CallAsync(server.Endpoint);

        Assert.Contains("CHƯA trả về cả header", result.ErrorMessage);
        Assert.Contains("Test Connection", result.ErrorMessage);
        Assert.DoesNotContain("chưa từng trả lời", result.ErrorMessage);
    }

    [Fact]
    public async Task ACutMidStream_IsExplainedAsATotalDurationLimit()
    {
        using var server = new CuttingServer(Cut.MidStream, TimeSpan.FromMilliseconds(100));

        var result = await CallAsync(server.Endpoint);

        Assert.False(result.IsSuccess);
        Assert.Contains("giữa lúc đang trả câu trả lời", result.ErrorMessage);
        // Đã có header rồi mới đứt thì SDK không thử lại — đúng một kết nối.
        Assert.Equal(1, server.Connections);
    }

    private sealed class NoBudgetLimit : IBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
