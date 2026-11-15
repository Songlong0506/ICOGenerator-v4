using System.Text.Json;
using ICOGenerator.Application.Models;
using ICOGenerator.Services.Budget;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

/// <summary>
/// Relay OpenAI-compatible cho agent CLI chạy NGOÀI app (opencode, ở lượt "Thử model khác" chọn vỏ
/// opencode — xem <c>PocOpencodeBuilder</c>). Đây không phải bề mặt cho người: không cookie, không quyền
/// RBAC, không antiforgery. Thứ duy nhất mở nó là token tạm trong header <c>Authorization: Bearer</c> do
/// app tự cấp cho đúng một lượt chạy (<c>LlmRelaySessions</c>) — token đó chỉ gọi được đúng MỘT model, và
/// mọi lời gọi đi qua cùng budget guard + call log như lời gọi của chính app.
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("opencode-relay/v1")]
public class OpencodeRelayController : Controller
{
    // Nhịp giữ kết nối khi model chưa ra token nào (model reasoning có thể im lặng nhiều phút): dòng comment
    // SSE bị mọi parser bỏ qua, nhưng giữ cho proxy/load balancer khỏi cắt một kết nối "im lặng".
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly RelayModelCallUseCase _relay;

    public OpencodeRelayController(RelayModelCallUseCase relay) => _relay = relay;

    [HttpPost("chat/completions")]
    public async Task ChatCompletions()
    {
        var aborted = HttpContext.RequestAborted;
        JsonDocument body;
        try
        {
            body = await JsonDocument.ParseAsync(Request.Body, cancellationToken: aborted);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(StatusCodes.Status400BadRequest, "Body không phải JSON.", "invalid_request_error");
            return;
        }

        using (body)
        {
            var call = await _relay.ExecuteAsync(BearerToken(), body.RootElement, aborted);
            switch (call.Status)
            {
                case RelayModelCallStatus.Unauthorized:
                    await WriteErrorAsync(StatusCodes.Status401Unauthorized, call.Error!, "authentication_error");
                    return;
                case RelayModelCallStatus.BadRequest:
                    await WriteErrorAsync(StatusCodes.Status400BadRequest, call.Error!, "invalid_request_error");
                    return;
                case RelayModelCallStatus.ModelUnavailable:
                    await WriteErrorAsync(StatusCodes.Status404NotFound, call.Error!, "not_found_error");
                    return;
            }

            if (!call.Stream)
            {
                string json;
                try
                {
                    json = await call.CompleteAsync(aborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !aborted.IsCancellationRequested)
                {
                    await WriteErrorAsync(StatusFor(ex), ex.Message, TypeFor(ex));
                    return;
                }

                Response.ContentType = "application/json";
                await Response.WriteAsync(json, aborted);
                return;
            }

            await StreamAsync(call, aborted);
        }
    }

    private async Task StreamAsync(RelayModelCall call, CancellationToken aborted)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await Response.Body.FlushAsync(aborted);

        await using var chunks = call.StreamChunksAsync(aborted).GetAsyncEnumerator(aborted);
        Task<bool>? pending = null;
        try
        {
            while (true)
            {
                pending ??= chunks.MoveNextAsync().AsTask();
                var finished = await Task.WhenAny(pending, Task.Delay(KeepAliveInterval, aborted));
                if (finished != pending)
                {
                    await Response.WriteAsync(": keep-alive\n\n", aborted);
                    await Response.Body.FlushAsync(aborted);
                    continue;
                }

                var hasNext = await pending;
                pending = null;
                if (!hasNext)
                    break;
                await Response.WriteAsync($"data: {chunks.Current}\n\n", aborted);
                await Response.Body.FlushAsync(aborted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !aborted.IsCancellationRequested)
        {
            // Header 200 đã đi rồi nên không đổi được status: báo lỗi bằng một frame theo hình lỗi của OpenAI
            // (AI SDK đọc frame này thành lỗi của lượt gọi) rồi đóng stream.
            try
            {
                await Response.WriteAsync($"data: {Services.Llm.OpenAiRelayProtocol.Error(ex.Message, TypeFor(ex))}\n\n", CancellationToken.None);
                await Response.Body.FlushAsync(CancellationToken.None);
            }
            catch (IOException)
            {
                // opencode đã đóng kết nối — không còn ai để báo.
            }
        }
    }

    private string? BearerToken()
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
    }

    private async Task WriteErrorAsync(int status, string message, string type)
    {
        Response.StatusCode = status;
        Response.ContentType = "application/json";
        await Response.WriteAsync(Services.Llm.OpenAiRelayProtocol.Error(message, type), HttpContext.RequestAborted);
    }

    // Hết ngân sách là 429 (client dừng, không thử lại vô ích như với 5xx); còn lại là lỗi phía upstream.
    private static int StatusFor(Exception ex) =>
        ex is BudgetExceededException ? StatusCodes.Status429TooManyRequests : StatusCodes.Status502BadGateway;

    private static string TypeFor(Exception ex) =>
        ex is BudgetExceededException ? "budget_exceeded" : "upstream_error";
}
