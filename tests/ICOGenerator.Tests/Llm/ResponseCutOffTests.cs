using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ICOGenerator.Domain;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Evals;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    // GatewayTimeout/Overloaded: gateway không đóng kết nối mà trả hẳn một mã HTTP (504 / 503).
    private enum Cut { BeforeHeaders, MidStream, Reset, GatewayTimeout, Overloaded }

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
                try { await ReadRequestAsync(stream); } catch { return; }

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
                if (cut is Cut.GatewayTimeout or Cut.Overloaded)
                {
                    // Connection: close ⇒ mỗi lần thử lại là một kết nối mới, nên Connections đếm đúng số lần thử.
                    var status = cut == Cut.GatewayTimeout ? "504 Gateway Timeout" : "503 Service Unavailable";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                    await stream.FlushAsync();
                }
                if (cut == Cut.Reset)
                    client.Client.LingerState = new LingerOption(true, 0); // đóng bằng RST: "forcibly closed by the remote host"
            }
        }

        // Đọc TRỌN request (header + body theo Content-Length). Một lần ReadAsync là không đủ: client có thể gửi
        // header và body thành hai gói, và đóng socket khi body còn trong bộ đệm thì Windows gửi RST — client thấy
        // "Could not send the request" thay vì đúng kiểu cắt mà test dựng.
        private static async Task ReadRequestAsync(NetworkStream stream)
        {
            var received = new List<byte>();
            var buffer = new byte[65536];
            int headerEnd;
            while ((headerEnd = IndexOfHeaderEnd(received)) < 0)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) return;
                received.AddRange(buffer.AsSpan(0, read).ToArray());
            }

            var headers = Encoding.ASCII.GetString(received.ToArray(), 0, headerEnd);
            var lengthLine = headers.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var remaining = (lengthLine is null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim()))
                - (received.Count - headerEnd - 4);
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) return;
                remaining -= read;
            }
        }

        private static int IndexOfHeaderEnd(List<byte> data)
        {
            for (var i = 0; i + 3 < data.Count; i++)
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                    return i;
            return -1;
        }

        public void Dispose() => _listener.Stop();
    }

    private static LlmClient Client()
    {
        var services = new ServiceCollection();
        // Như client "llm-direct" thật: không proxy. Để handler mặc định thì trên máy có PAC công ty, request
        // loopback đi qua proxy và proxy biến kiểu cắt mà test dựng thành một mã 503 của chính nó.
        services.AddHttpClient(OpenAIChatClientFactory.DirectClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });
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

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in SlowCutOffClient(server.Endpoint).GetStreamingResponseAsync("hi")) { }
        });

        Assert.Equal(1, server.Connections);
    }

    // Gateway giữ lời gọi rồi RESET kết nối (ca thật: Model Farm trước claude-sonnet-5-5, ~300s mỗi lần, bốn
    // lần thử = 20 phút) — cùng loại "hết kiên nhẫn" như cắt bằng FIN, nên cũng không thử lại.
    [Fact]
    public async Task ASlowReset_IsNotRetried()
    {
        using var server = new CuttingServer(Cut.Reset, TimeSpan.FromMilliseconds(600));

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in SlowCutOffClient(server.Endpoint).GetStreamingResponseAsync("hi")) { }
        });

        Assert.Equal(1, server.Connections);
    }

    // Ngưỡng 300ms để ca "chậm" (server chờ 600ms) khỏi phải chờ thật lâu; ca "nhanh" truyền ngưỡng rộng hơn vì
    // khi cả bộ test chạy song song, một lần thử lẽ ra tức thì vẫn có thể vượt 300ms.
    private static IChatClient SlowCutOffClient(string endpoint, ILoggerFactory? loggers = null, int slowCutOffMs = 300) =>
        new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            // Không proxy — cùng lý do như Client(): PAC công ty kéo cả loopback qua proxy.
            Transport = new HttpClientPipelineTransport(new HttpClient(new SocketsHttpHandler { UseProxy = false })),
            RetryPolicy = new LlmRetryPolicy(TimeSpan.FromMilliseconds(slowCutOffMs), loggers?.CreateLogger<LlmRetryPolicy>()),
        }).GetChatClient("m").AsIChatClient();

    // Gateway không đóng kết nối mà tự trả 504 sau khi chờ upstream đủ ngưỡng — cùng loại "hết kiên nhẫn". Ca thật
    // 2026-11-26: một dòng call log 748s cho 22k token, khớp hai lần thử ~300s bị SDK thử lại trong im lặng.
    [Fact]
    public async Task ASlowGatewayStatus_IsNotRetried_AndIsLogged()
    {
        using var server = new CuttingServer(Cut.GatewayTimeout, TimeSpan.FromMilliseconds(600));
        var loggers = new CapturingLoggerFactory();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in SlowCutOffClient(server.Endpoint, loggers).GetStreamingResponseAsync("hi")) { }
        });

        Assert.Equal(1, server.Connections);
        var warning = Assert.Single(loggers.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("attempt 1 failed", warning.Message);
        Assert.Contains("HTTP 504", warning.Message);
        Assert.Contains("not retrying", warning.Message);
    }

    // 503 quá tải trả về NGAY thì thử lại vẫn đáng — và mỗi lần thử hỏng đều để lại một dòng Warning.
    [Fact]
    public async Task AFastGatewayStatus_IsStillRetried_AndEveryAttemptIsLogged()
    {
        using var server = new CuttingServer(Cut.Overloaded, TimeSpan.Zero);
        var loggers = new CapturingLoggerFactory();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in SlowCutOffClient(server.Endpoint, loggers, slowCutOffMs: 5000).GetStreamingResponseAsync("hi")) { }
        });

        Assert.Equal(4, server.Connections);
        var warnings = loggers.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        Assert.Equal(4, warnings.Count);
        Assert.Contains("retrying", warnings[0]);
        Assert.Contains("attempt 4 failed", warnings[3]);
    }

    // Không có nổi header dù lời gọi đi dạng stream ⇒ gateway giữ cả phản hồi. Gợi ý "hạ Structured Output"
    // ở ca này là chỉ sai chỗ — người dùng làm theo, lỗi vẫn y nguyên.
    [Fact]
    public async Task ACutBeforeHeaders_IsExplainedAsTheGatewayHoldingTheWholeResponse()
    {
        using var server = new CuttingServer(Cut.BeforeHeaders, TimeSpan.Zero);

        var result = await CallAsync(server.Endpoint);

        Assert.Contains("WITHOUT returning even the response headers", result.ErrorMessage);
        Assert.Contains("Test Connection", result.ErrorMessage);
        Assert.DoesNotContain("never answered", result.ErrorMessage);
    }

    [Fact]
    public async Task ACutMidStream_IsExplainedAsATotalDurationLimit()
    {
        using var server = new CuttingServer(Cut.MidStream, TimeSpan.FromMilliseconds(100));

        var result = await CallAsync(server.Endpoint);

        Assert.False(result.IsSuccess);
        Assert.Contains("while sending the answer", result.ErrorMessage);
        // Đã có header rồi mới đứt thì SDK không thử lại — đúng một kết nối.
        Assert.Equal(1, server.Connections);
    }

    private sealed class NoBudgetLimit : IBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
