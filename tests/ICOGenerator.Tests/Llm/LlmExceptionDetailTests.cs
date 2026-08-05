using System.Net.Sockets;
using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Lỗi mạng tới tay người dùng dưới dạng vô dụng nhất có thể: "Retry failed after 4 tries. (An error
// occurred while sending the request.) ×4" — bốn bản sao của cùng một câu CHUNG CHUNG, còn nguyên nhân thật
// (kết nối bị đóng / TLS / DNS) nằm ở InnerException mà không ai in ra. Các test này khóa việc chuỗi nguyên
// nhân phải đi được xuống tới đáy, vì đó là thứ duy nhất phân biệt được các sự cố với nhau.
public class LlmExceptionDetailTests
{
    private static Exception RetryWrapped(Exception cause) =>
        new AggregateException(
            "Retry failed after 4 tries.",
            Enumerable.Range(0, 4).Select(_ => (Exception)new HttpRequestException(
                "An error occurred while sending the request.", cause)));

    [Fact]
    public void Describe_DigsOutTheRealCause_BehindTheRetryWrapper()
    {
        var detail = LlmExceptionDetail.Describe(RetryWrapped(
            new SocketException(10054, "An existing connection was forcibly closed by the remote host")));

        Assert.Contains("Retry failed after 4 tries.", detail);
        Assert.Contains("An error occurred while sending the request.", detail);
        // Điều duy nhất thật sự có ích — và là điều bản cũ đánh rơi.
        Assert.Contains("forcibly closed by the remote host", detail);
        Assert.Contains(nameof(SocketException), detail);
    }

    // Chính sách retry gói BỐN lần thử giống hệt nhau; in cả bốn chỉ làm loãng bong bóng lỗi.
    [Fact]
    public void Describe_DoesNotRepeatTheSameMessageOncePerRetry()
    {
        var detail = LlmExceptionDetail.Describe(RetryWrapped(new IOException("The response ended prematurely.")));

        Assert.Equal(1, detail.Split("An error occurred while sending the request.").Length - 1);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(IOException))]
    public void IsTransportFailure_SeesThroughTheRetryWrapper(Type causeType)
    {
        var cause = (Exception)Activator.CreateInstance(causeType)!;
        Assert.True(LlmExceptionDetail.IsTransportFailure(RetryWrapped(cause)));
    }

    // Hỏng khi dựng schema / SDK ném lỗi logic cũng KHÔNG có mã HTTP — nhưng bỏ ảnh gửi lại chẳng cứu được
    // gì, chỉ tốn thêm một round-trip. Phân loại theo họ exception chính là chỗ chặn nhầm lẫn đó.
    [Fact]
    public void IsTransportFailure_IsFalse_ForAnOrdinaryLogicError()
    {
        Assert.False(LlmExceptionDetail.IsTransportFailure(
            new InvalidOperationException("schema generation failed")));
    }
}
