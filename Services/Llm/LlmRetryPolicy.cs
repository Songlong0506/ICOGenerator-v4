using System.ClientModel.Primitives;
using System.Diagnostics;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Chính sách thử lại mặc định của SDK (3 lần thử lại, backoff mũ), trừ MỘT ca: endpoint nhận request rồi đóng
/// kết nối (<see cref="LlmExceptionDetail.IsResponseCutOff"/>) sau khi đã giữ nó một lúc lâu.
///
/// <para>
/// Ca đó là gateway/proxy đứng trước endpoint cắt lời gọi khi chạm ngưỡng timeout của nó — một ngưỡng CỐ ĐỊNH,
/// nên lần thử lại nào cũng chờ đủ ngưỡng rồi bị cắt y hệt: bốn lần thử biến một lỗi ~35s thành ~140s người
/// dùng ngồi nhìn bong bóng "đang gõ", không đổi lấy được gì. Ngược lại, cùng lỗi đó mà tới NGAY (kết nối
/// keep-alive trong pool đã bị server đóng từ trước) thì thử lại trên kết nối mới gần như chắc chắn qua — nên
/// phân biệt theo thời gian của chính lần thử, không chặn theo loại lỗi.
/// </para>
/// </summary>
public sealed class LlmRetryPolicy : ClientRetryPolicy
{
    /// <summary>Một lần thử bị cắt sau ngần này là "gateway hết kiên nhẫn", không phải kết nối cũ.</summary>
    public static readonly TimeSpan DefaultSlowCutOff = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _slowCutOff;

    public LlmRetryPolicy() : this(DefaultSlowCutOff) { }

    /// <param name="slowCutOff">Ngưỡng tách "cắt chậm" khỏi "cắt nhanh" — test truyền số nhỏ để khỏi phải chờ.</param>
    public LlmRetryPolicy(TimeSpan slowCutOff) : base(maxRetries: 3) => _slowCutOff = slowCutOff;

    protected override void OnSendingRequest(PipelineMessage message)
    {
        message.SetProperty(typeof(AttemptStart), new AttemptStart(Stopwatch.GetTimestamp()));
        base.OnSendingRequest(message);
    }

    protected override ValueTask OnSendingRequestAsync(PipelineMessage message)
    {
        message.SetProperty(typeof(AttemptStart), new AttemptStart(Stopwatch.GetTimestamp()));
        return base.OnSendingRequestAsync(message);
    }

    protected override bool ShouldRetry(PipelineMessage message, Exception? exception) =>
        !IsSlowCutOff(message, exception) && base.ShouldRetry(message, exception);

    protected override async ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception) =>
        !IsSlowCutOff(message, exception) && await base.ShouldRetryAsync(message, exception).ConfigureAwait(false);

    private bool IsSlowCutOff(PipelineMessage message, Exception? exception) =>
        exception != null
        && LlmExceptionDetail.IsResponseCutOff(exception)
        && message.TryGetProperty(typeof(AttemptStart), out var start)
        && start is AttemptStart { Timestamp: var timestamp }
        && Stopwatch.GetElapsedTime(timestamp) >= _slowCutOff;

    private sealed record AttemptStart(long Timestamp);
}
