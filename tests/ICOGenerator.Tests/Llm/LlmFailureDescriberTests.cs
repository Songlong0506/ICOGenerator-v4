using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Sockets;
using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Chốt cách app dịch một exception của SDK sang câu người dùng đọc được. Bug gốc: lượt gọi kèm tài liệu
// .docx chết ở tầng transport, SDK gói lại thành "Retry failed after 4 tries. (An error occurred while
// sending the request.) …" và câu đó bị chép NGUYÊN VĂN vào bong bóng ⚠️ trong chat — người dùng không có
// cách nào biết là hỏng mạng, hỏng TLS hay request quá lớn.
public class LlmFailureDescriberTests
{
    // Đúng hình dạng SDK ném ra: HttpRequestException thật nằm dưới hai lớp bọc.
    private static Exception RetryExhausted() =>
        new AggregateException(
            "Retry failed after 4 tries. (An error occurred while sending the request.) (An error occurred while sending the request.)",
            new HttpRequestException("An error occurred while sending the request.",
                new IOException("The response ended prematurely.")));

    [Fact]
    public void Describe_UnwrapsRetryWrapper_AndClassifiesAsTransport()
    {
        var failure = LlmFailureDescriber.Describe(RetryExhausted(), timeoutSeconds: 600);

        Assert.Equal(LlmFailureKind.Transport, failure.Kind);
        Assert.Null(failure.Status);
        Assert.Contains("Không kết nối được tới endpoint", failure.Message);
        Assert.Equal("An error occurred while sending the request.", failure.Detail);
    }

    [Fact]
    public void Describe_SocketException_IsTransport()
    {
        var failure = LlmFailureDescriber.Describe(new SocketException(10061), timeoutSeconds: 600);

        Assert.Equal(LlmFailureKind.Transport, failure.Kind);
    }

    // Status 0 nghĩa là SDK không nhận được response nào — phải đi tiếp xuống inner, không được báo "API trả lỗi 0".
    [Fact]
    public void Describe_ClientResultExceptionWithoutStatus_FallsThroughToInnerCause()
    {
        var ex = new AggregateException("wrapper", new HttpRequestException("connection reset"));

        var failure = LlmFailureDescriber.Describe(ex, timeoutSeconds: 600);

        Assert.Equal(LlmFailureKind.Transport, failure.Kind);
        Assert.Equal("connection reset", failure.Detail);
    }

    [Fact]
    public void Describe_Timeout_UsesTheConfiguredDeadline()
    {
        var failure = LlmFailureDescriber.Describe(new OperationCanceledException(), timeoutSeconds: 42);

        Assert.Equal(LlmFailureKind.Timeout, failure.Kind);
        Assert.Contains("42s", failure.Message);
    }

    [Fact]
    public void Describe_ApiError_KeepsStatusAndExplainsIt()
    {
        var failure = LlmFailureDescriber.Describe(
            new ClientResultException("nope", new FakeResponse(401)), timeoutSeconds: 600);

        Assert.Equal(LlmFailureKind.Api, failure.Kind);
        Assert.Equal(401, failure.Status);
        Assert.Contains("ApiKey sai", failure.Message);
    }

    // 413 là câu trả lời TỬ TẾ cho request quá lớn (endpoint nào đóng thẳng kết nối thì rơi vào nhánh
    // Transport ở trên) — nói đúng nguyên nhân để người dùng biết đường hạ trần ảnh.
    [Fact]
    public void DescribeStatus_413_PointsAtTheOversizedRequest()
    {
        Assert.Contains("quá lớn", LlmFailureDescriber.DescribeStatus(413));
    }

    [Fact]
    public void Describe_UnknownException_KeepsItsOwnMessage()
    {
        var failure = LlmFailureDescriber.Describe(new InvalidOperationException("boom"), timeoutSeconds: 600);

        Assert.Equal(LlmFailureKind.Unknown, failure.Kind);
        Assert.Equal("boom", failure.Message);
    }

    private sealed class FakeResponse : PipelineResponse
    {
        public FakeResponse(int status) => Status = status;

        public override int Status { get; }
        public override string ReasonPhrase => string.Empty;
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => BinaryData.FromString(string.Empty);
        protected override PipelineResponseHeaders HeadersCore => throw new NotSupportedException();
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(Content);
        public override void Dispose() { }
    }
}
