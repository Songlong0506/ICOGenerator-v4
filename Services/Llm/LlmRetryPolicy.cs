using System.ClientModel.Primitives;
using System.Diagnostics;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Chính sách thử lại mặc định của SDK (3 lần thử lại, backoff mũ), trừ MỘT ca: lần thử hỏng SAU khi đã giữ
/// request một lúc lâu (<see cref="IsSlowGatewayFailure"/>).
///
/// <para>
/// Ca đó là gateway/proxy đứng trước endpoint cắt lời gọi khi chạm ngưỡng timeout của nó — một ngưỡng CỐ ĐỊNH,
/// nên lần thử lại nào cũng chờ đủ ngưỡng rồi bị cắt y hệt: bốn lần thử biến một lỗi ~35s thành ~140s người
/// dùng ngồi nhìn bong bóng "đang gõ", không đổi lấy được gì. Ngược lại, cùng lỗi đó mà tới NGAY (kết nối
/// keep-alive trong pool đã bị server đóng từ trước, 503 quá tải trả về tức thì) thì thử lại trên kết nối mới
/// gần như chắc chắn qua — nên phân biệt theo thời gian của chính lần thử, không chặn theo loại lỗi.
/// </para>
///
/// <para>
/// Gateway cắt theo HAI kiểu và cả hai đều tính: đóng/reset kết nối (lỗi tầng truyền tải), hoặc trả hẳn một mã
/// HTTP (504/502 do proxy tự sinh khi upstream im lặng quá lâu). Trước đây chỉ kiểu đầu bị chặn, nên kiểu sau
/// vẫn được SDK thử lại trong im lặng và cộng dồn vào MỘT dòng call log — ca thật 2026-11-26: Developer bước 1
/// ghi 748s cho 22k token (~29 token/s, tốc độ thường ~150), khớp với hai lần thử ~300s bị nuốt rồi một lần
/// chạy bình thường. Mỗi lần thử hỏng giờ được ghi Warning (lên màn System Logs) để thời gian đó không còn vô hình.
/// </para>
/// </summary>
public sealed class LlmRetryPolicy : ClientRetryPolicy
{
    /// <summary>Một lần thử hỏng sau ngần này là "gateway hết kiên nhẫn", không phải kết nối cũ.</summary>
    public static readonly TimeSpan DefaultSlowCutOff = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _slowCutOff;
    private readonly ILogger? _logger;

    public LlmRetryPolicy(ILogger? logger = null) : this(DefaultSlowCutOff, logger) { }

    /// <param name="slowCutOff">Ngưỡng tách "cắt chậm" khỏi "cắt nhanh" — test truyền số nhỏ để khỏi phải chờ.</param>
    /// <param name="logger">Null ⇒ không ghi log (test, hoặc caller không có DI).</param>
    public LlmRetryPolicy(TimeSpan slowCutOff, ILogger? logger = null) : base(maxRetries: 3)
    {
        _slowCutOff = slowCutOff;
        _logger = logger;
    }

    protected override void OnSendingRequest(PipelineMessage message)
    {
        Begin(message);
        base.OnSendingRequest(message);
    }

    protected override ValueTask OnSendingRequestAsync(PipelineMessage message)
    {
        Begin(message);
        return base.OnSendingRequestAsync(message);
    }

    // Chỉ override bản đồng bộ: ShouldRetryAsync mặc định của SDK gọi lại chính method này, nên override cả hai
    // thì đường async ghi log mỗi lần thử HAI lần. Test ASlowGatewayStatus (đường stream, async) giữ cho luật
    // vẫn áp ở đường async nếu một bản SDK sau đổi điều đó.
    protected override bool ShouldRetry(PipelineMessage message, Exception? exception)
    {
        var slow = IsSlowGatewayFailure(message, exception);
        return Report(message, exception, slow, retry: !slow && base.ShouldRetry(message, exception));
    }

    // Số thứ tự lần thử + mốc của lần ĐẦU đi theo message, vì một instance policy phục vụ mọi lời gọi của client.
    private static void Begin(PipelineMessage message)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = message.TryGetProperty(typeof(Attempt), out var value) ? value as Attempt : null;
        message.SetProperty(typeof(Attempt), new Attempt((previous?.Number ?? 0) + 1, now, previous?.FirstStart ?? now));
    }

    private static Attempt? CurrentAttempt(PipelineMessage message) =>
        message.TryGetProperty(typeof(Attempt), out var value) ? value as Attempt : null;

    private bool IsSlowGatewayFailure(PipelineMessage message, Exception? exception)
    {
        if (CurrentAttempt(message) is not { } attempt || Stopwatch.GetElapsedTime(attempt.Start) < _slowCutOff)
            return false;

        // 429 có Retry-After riêng và hiếm khi tới chậm — để SDK lo như cũ.
        return exception != null
            ? LlmExceptionDetail.IsTransportFailure(exception)
            : message.Response is { Status: 408 or >= 500 };
    }

    private bool Report(PipelineMessage message, Exception? exception, bool slowGatewayFailure, bool retry)
    {
        if (_logger is null || CurrentAttempt(message) is not { } attempt)
            return retry;

        var elapsed = Stopwatch.GetElapsedTime(attempt.Start).TotalSeconds;
        var host = message.Request.Uri?.Host;
        var failed = exception != null || message.Response?.IsError == true;

        if (failed)
        {
            var reason = exception != null
                ? LlmExceptionDetail.Describe(exception)
                : $"HTTP {message.Response!.Status} {message.Response.ReasonPhrase}";
            var decision = retry ? "retrying"
                : slowGatewayFailure ? "not retrying: a cut after a long wait is a fixed gateway/proxy timeout and would repeat"
                : "not retrying";
            _logger.LogWarning("LLM call to {Host}: attempt {Attempt} failed after {Elapsed:0.0}s ({Reason}) — {Decision}.",
                host, attempt.Number, elapsed, reason, decision);
        }
        else if (attempt.Number > 1)
        {
            _logger.LogInformation("LLM call to {Host}: attempt {Attempt} got a response after {Elapsed:0.0}s ({Total:0.0}s since the first attempt).",
                host, attempt.Number, elapsed, Stopwatch.GetElapsedTime(attempt.FirstStart).TotalSeconds);
        }
        else if (!message.BufferResponse && elapsed >= _slowCutOff.TotalSeconds)
        {
            // Lời gọi stream bình thường có header sau vài giây; chậm hơn ngưỡng là upstream đang xếp hàng.
            _logger.LogInformation("LLM call to {Host}: streaming response headers arrived only after {Elapsed:0.0}s.",
                host, elapsed);
        }

        return retry;
    }

    private sealed record Attempt(int Number, long Start, long FirstStart);
}
