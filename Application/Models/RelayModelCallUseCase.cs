using System.Runtime.CompilerServices;
using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Application.Models;

public enum RelayModelCallStatus { Ok, Unauthorized, BadRequest, ModelUnavailable }

/// <summary>
/// Lời gọi đã được chấp nhận: <see cref="Stream"/> = trả SSE (đọc <see cref="StreamChunksAsync"/>), ngược lại
/// trả một JSON trọn (<see cref="CompleteAsync"/>). Lỗi của CHÍNH lời gọi model (budget guard, endpoint hỏng,
/// timeout) bay ra dạng exception từ hai hàm đó — controller đổi nó thành thân lỗi kiểu OpenAI.
/// </summary>
public sealed class RelayModelCall
{
    public RelayModelCallStatus Status { get; init; }
    public string? Error { get; init; }
    public bool Stream { get; init; }
    public Func<CancellationToken, IAsyncEnumerable<string>> StreamChunksAsync { get; init; } = _ => NoChunks();
    public Func<CancellationToken, Task<string>> CompleteAsync { get; init; } = _ => Task.FromResult(string.Empty);

    public static RelayModelCall Fail(RelayModelCallStatus status, string error) => new() { Status = status, Error = error };

    private static async IAsyncEnumerable<string> NoChunks()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// Một lời gọi <c>/chat/completions</c> của agent CLI bên ngoài (opencode — xem <c>PocOpencodeBuilder</c>)
/// đi qua relay của app: đổi token tạm (<see cref="LlmRelaySessions"/>) ra model + dự án + agent, rồi chạy
/// lời gọi trên CHÍNH đường ống mọi lời gọi khác của app dùng (<see cref="IChatClientFactory"/> +
/// <see cref="ModelCallLoggingChatClient"/>) — nên budget guard chặn được, call log + trang Usage thấy được,
/// proxy/Vertex/vá request đều áp như cũ, và API key không bao giờ rời app.
/// </summary>
public class RelayModelCallUseCase
{
    private readonly AppDbContext _db;
    private readonly LlmRelaySessions _sessions;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IModelCallLogger _modelCallLogger;
    private readonly IBudgetGuard _budgetGuard;
    private readonly LlmSettings _llmSettings;

    public RelayModelCallUseCase(AppDbContext db, LlmRelaySessions sessions, IChatClientFactory chatClientFactory,
        IModelCallLogger modelCallLogger, IBudgetGuard budgetGuard, LlmSettings llmSettings)
    {
        _db = db;
        _sessions = sessions;
        _chatClientFactory = chatClientFactory;
        _modelCallLogger = modelCallLogger;
        _budgetGuard = budgetGuard;
        _llmSettings = llmSettings;
    }

    public async Task<RelayModelCall> ExecuteAsync(string? token, JsonElement body, CancellationToken cancellationToken = default)
    {
        var session = _sessions.Resolve(token);
        if (session == null)
            return RelayModelCall.Fail(RelayModelCallStatus.Unauthorized, "Token relay không hợp lệ hoặc lượt chạy đã kết thúc.");

        var request = OpenAiRelayProtocol.TryParseRequest(body, out var parseError);
        if (request == null)
            return RelayModelCall.Fail(RelayModelCallStatus.BadRequest, parseError);

        var model = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == session.AiModelId, cancellationToken);
        var agent = await _db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == session.AgentId, cancellationToken);
        if (model == null || agent == null)
            return RelayModelCall.Fail(RelayModelCallStatus.ModelUnavailable, "Model hoặc agent của lượt chạy đã bị xoá.");

        // Model trả lời luôn mang tên model THẬT, không phải tên mà agent bên ngoài tự đặt trong cấu hình của nó.
        request.Options.ModelId = model.ModelId;
        IChatClient inner;
        try
        {
            inner = _chatClientFactory.Create(model);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or InvalidOperationException)
        {
            // Cấu hình model hỏng (thiếu API key, Endpoint không phải URL…): trả lý do đọc được cho agent bên
            // ngoài — nó in lỗi ra log của lượt chạy, và lỗi đó đi vào cột Lỗi của bảng so sánh.
            return RelayModelCall.Fail(RelayModelCallStatus.ModelUnavailable,
                $"Model '{model.ModelId}' chưa cấu hình được cho lời gọi (kiểm tra Endpoint/API key ở màn AI Models): {ex.Message}");
        }

        var client = inner
            .AsBuilder()
            .Use(inner => new ModelCallLoggingChatClient(
                inner, model, _modelCallLogger,
                new ModelCallLogContext(session.ProjectId, agent, session.Purpose, session.WorkflowRunId),
                new ModelCallOptions(_llmSettings.RequestTimeoutSeconds, ThrowOnFailure: true) { BudgetGuard = _budgetGuard }))
            .Build();

        var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new RelayModelCall
        {
            Status = RelayModelCallStatus.Ok,
            Stream = request.Stream,
            StreamChunksAsync = ct => StreamAsync(client, request, id, model.ModelId, created, ct),
            CompleteAsync = async ct =>
            {
                var response = await client.GetResponseAsync(request.Messages, request.Options, ct);
                return OpenAiRelayProtocol.Completion(id, model.ModelId, created, response);
            }
        };
    }

    private static async IAsyncEnumerable<string> StreamAsync(IChatClient client, RelayChatRequest request, string id,
        string modelId, long created, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var toolIndex = 0;
        var roleSent = false;
        ChatFinishReason? finish = null;
        UsageDetails? usage = null;
        await foreach (var update in client.GetStreamingResponseAsync(request.Messages, request.Options, cancellationToken))
        {
            finish = update.FinishReason ?? finish;
            foreach (var content in update.Contents.OfType<UsageContent>())
            {
                usage ??= new UsageDetails();
                usage.Add(content.Details);
            }

            var chunk = OpenAiRelayProtocol.UpdateChunk(id, modelId, created, update, ref toolIndex, ref roleSent);
            if (chunk != null)
                yield return chunk;
        }

        yield return OpenAiRelayProtocol.FinishChunk(id, modelId, created, OpenAiRelayProtocol.FinishReason(finish, toolIndex > 0));
        if (usage != null)
            yield return OpenAiRelayProtocol.UsageChunk(id, modelId, created, usage);
        yield return "[DONE]";
    }
}
