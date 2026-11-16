using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Agents;

/// <summary>
/// Middleware ghi <see cref="Domain.AgentRunCheckpoint"/> quanh MỖI lời gọi model của một lượt agent. Đứng
/// giữa vòng lặp gọi tool (<c>FunctionInvokingChatClient</c>) và lời gọi model, nên nó nhìn thấy đúng thứ
/// cần giữ: danh sách message mà vòng lặp sắp gửi — tức mọi bước đã xong kèm kết quả tool của chúng.
///
/// <para>Hai mốc ghi mỗi bước, vì lượt chạy có thể chết ở hai chỗ khác nhau:</para>
/// <list type="bullet">
///   <item><b>Trước lời gọi model</b>: ghi request. Lời gọi hỏng (hạn mức, rớt kết nối — ca thường gặp nhất)
///         thì checkpoint khớp tuyệt đối với đĩa, và lượt chạy tiếp gửi lại đúng request đó.</item>
///   <item><b>Sau khi model trả lời có gọi tool</b>: ghi thêm lượt trả lời. Nếu app chết TRONG lúc tool
///         chạy, lượt chạy tiếp thấy các lời gọi tool còn treo và báo cho model là "không rõ kết quả" (xem
///         <see cref="AgentRunCheckpointStore.ResolveInterruptedToolCalls"/>) thay vì âm thầm gọi lại một
///         tool đã ghi ra đĩa.</item>
/// </list>
///
/// <para>
/// Lượt trả lời CUỐI (không gọi tool) cố ý không được ghi: hội thoại kết thúc bằng lời của assistant thì
/// không có gì để "chạy tiếp". Nếu việc hậu kỳ của task hỏng sau đó, lượt chạy tiếp chỉ xin lại đúng câu
/// trả lời cuối — một lời gọi, không làm lại bước nào.
/// </para>
/// </summary>
public sealed class CheckpointingChatClient : DelegatingChatClient
{
    private readonly AgentRunCheckpointStore _store;
    private readonly Guid _taskId;
    private readonly string? _modelId;
    private int _completedSteps;

    /// <param name="completedSteps">Số bước đã xong ở các lần chạy trước (0 cho lượt mới).</param>
    public CheckpointingChatClient(IChatClient inner, AgentRunCheckpointStore store, Guid taskId, string? modelId, int completedSteps)
        : base(inner)
    {
        _store = store;
        _taskId = taskId;
        _modelId = modelId;
        _completedSteps = completedSteps;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        await _store.SaveAsync(_taskId, request, _completedSteps, _modelId).ConfigureAwait(false);

        var response = await base.GetResponseAsync(request, options, cancellationToken).ConfigureAwait(false);

        await SaveAfterResponseAsync(request, response).ConfigureAwait(false);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        await _store.SaveAsync(_taskId, request, _completedSteps, _modelId).ConfigureAwait(false);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in base.GetStreamingResponseAsync(request, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            yield return update;
        }

        await SaveAfterResponseAsync(request, updates.ToChatResponse()).ConfigureAwait(false);
    }

    private async Task SaveAfterResponseAsync(IReadOnlyList<ChatMessage> request, ChatResponse response)
    {
        _completedSteps++;
        if (!response.Messages.Any(m => m.Contents.Any(c => c is FunctionCallContent)))
            return;

        var withResponse = new List<ChatMessage>(request.Count + response.Messages.Count);
        withResponse.AddRange(request);
        withResponse.AddRange(response.Messages);
        await _store.SaveAsync(_taskId, withResponse, _completedSteps, _modelId).ConfigureAwait(false);
    }
}
