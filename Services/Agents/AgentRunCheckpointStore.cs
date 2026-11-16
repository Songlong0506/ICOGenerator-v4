using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Agents;

/// <summary>
/// Chỗ một lượt agent chạy tiếp: hội thoại đã khôi phục + số bước đã tiêu. Dựng bởi
/// <see cref="AgentRunCheckpointStore.TryLoadAsync"/>, tiêu thụ bởi <see cref="AgentRunService.RunAsync"/>.
/// </summary>
/// <param name="Messages">Hội thoại gửi cho lời gọi model kế tiếp — đã vá các lời gọi tool còn treo.</param>
/// <param name="CompletedSteps">Số lời gọi model đã thành công ở (các) lần chạy trước; luôn &gt; 0.</param>
/// <param name="ModelId">Model đã chạy các bước đó (null = không rõ).</param>
public sealed record AgentRunResumePoint(IReadOnlyList<ChatMessage> Messages, int CompletedSteps, string? ModelId)
{
    /// <summary>
    /// Hội thoại để gửi cho <paramref name="modelId"/>. Cùng model ⇒ nguyên văn. Khác model ⇒ gỡ phần suy
    /// luận ẩn: nó mang chữ ký của đúng model đã sinh ra nó, model khác nhận vào là từ chối cả request —
    /// và đổi model chính là việc người ta hay làm nhất sau khi một model chạm hạn mức.
    /// </summary>
    public IReadOnlyList<ChatMessage> MessagesFor(string modelId)
    {
        if (ModelId == null || string.Equals(ModelId, modelId, StringComparison.Ordinal))
            return Messages;

        var result = new List<ChatMessage>(Messages.Count);
        foreach (var message in Messages)
        {
            if (!message.Contents.Any(c => c is TextReasoningContent))
            {
                result.Add(message);
                continue;
            }

            var kept = message.Contents.Where(c => c is not TextReasoningContent).ToList();
            // Lượt chỉ có suy luận ẩn thì bỏ hẳn: một message rỗng cũng bị endpoint từ chối.
            if (kept.Count > 0)
                result.Add(new ChatMessage(message.Role, kept) { AuthorName = message.AuthorName, MessageId = message.MessageId });
        }

        return result;
    }
}

/// <summary>
/// Bật checkpoint bền cho một lượt <see cref="AgentRunService.RunAsync"/>.
/// </summary>
/// <param name="TaskId">Task sở hữu checkpoint — mỗi task một dòng, ghi đè sau mỗi bước.</param>
/// <param name="ResumeFrom">Có ⇒ chạy TIẾP hội thoại này thay vì bắt đầu lượt mới. Null ⇒ lượt mới.</param>
public sealed record AgentRunCheckpointScope(Guid TaskId, AgentRunResumePoint? ResumeFrom = null);

/// <summary>
/// Đọc/ghi <see cref="AgentRunCheckpoint"/> — điểm dừng bền của một lượt agent.
///
/// <para>
/// <b>Luôn dùng DbContext RIÊNG</b> (một scope mới mỗi thao tác) chứ không phải context của lượt chạy: checkpoint
/// phải ghi được đúng lúc context kia đang dở một giao dịch hoặc đã hỏng, và không được kéo các entity đang
/// track của worker vào lần <c>SaveChanges</c> của mình.
/// </para>
///
/// <para>
/// <b>Ghi là fail-open.</b> Một lần ghi lỗi chỉ được log: mỗi lần ghi là một bản CHỤP ĐẦY ĐỦ nên lần ghi kế
/// tự vá lại, còn để nó ném ra thì một trục trặc DB thoáng qua giết luôn lượt chạy mà nó sinh ra để bảo vệ.
/// </para>
/// </summary>
public class AgentRunCheckpointStore
{
    /// <summary>
    /// Kết quả ghi thay cho một lời gọi tool mà lượt chạy trước bị ngắt KHI ĐANG CHẠY nó (app tắt/crash giữa
    /// chừng). Không ai biết tool đó đã kịp ghi ra đĩa hay chưa — nói thật điều đó để model kiểm tra lại,
    /// thay vì đoán "đã xong" (bỏ sót việc) hay "chưa chạy" (ghi trùng, vd AppendPocContent hai lần).
    /// </summary>
    public const string InterruptedToolResult =
        "KHÔNG RÕ KẾT QUẢ: lượt chạy trước bị ngắt đúng lúc tool này đang chạy, nên không biết nó đã kịp ghi "
        + "thay đổi hay chưa. Hãy KIỂM TRA trạng thái thật (đọc lại file / chạy lại bước tự kiểm) trước khi "
        + "quyết định có gọi lại tool này không — đừng gọi lại ngay, vì có thể ghi trùng.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentRunCheckpointStore> _logger;

    public AgentRunCheckpointStore(IServiceScopeFactory scopeFactory, ILogger<AgentRunCheckpointStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Ghi đè checkpoint của task. KHÔNG nhận CancellationToken: lần ghi rơi đúng lúc app đang tắt là lần
    /// ghi quan trọng nhất — bỏ nó thì checkpoint lùi một bước so với đĩa và lượt chạy tiếp lặp lại tool đã chạy.
    /// </summary>
    public virtual async Task SaveAsync(Guid taskId, IReadOnlyList<ChatMessage> messages, int completedSteps, string? modelId)
    {
        try
        {
            var json = Serialize(messages);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Gắn thẳng một entity mới thay vì đọc dòng cũ: hội thoại cỡ vài trăm KB, và đọc nó lên chỉ để
            // ghi đè là phí. Chỉ lượt đang chạy task này ghi dòng này (worker claim task nguyên tử), nên
            // không có cuộc đua "Any rồi Add".
            var exists = await db.AgentRunCheckpoints.AnyAsync(x => x.TaskId == taskId);
            var entity = new AgentRunCheckpoint
            {
                TaskId = taskId,
                MessagesJson = json,
                CompletedSteps = completedSteps,
                ModelId = modelId,
                UpdatedAt = DateTime.UtcNow
            };
            db.Entry(entity).State = exists ? EntityState.Modified : EntityState.Added;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không ghi được checkpoint của agent task {TaskId} (bước {Step}).", taskId, completedSteps);
        }
    }

    /// <summary>
    /// Điểm chạy tiếp của task, hoặc null khi không có gì để nối: chưa có checkpoint, lượt trước chưa xong
    /// bước nào, hoặc checkpoint không đọc lại được (khi đó nó bị xoá để lần sau khỏi vấp lại).
    /// </summary>
    public virtual async Task<AgentRunResumePoint?> TryLoadAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.AgentRunCheckpoints.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TaskId == taskId, cancellationToken);
        if (row == null || row.CompletedSteps <= 0)
            return null;

        try
        {
            var messages = Deserialize(row.MessagesJson);
            if (messages.Count > 0)
                return new AgentRunResumePoint(ResolveInterruptedToolCalls(messages), row.CompletedSteps, row.ModelId);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Checkpoint của agent task {TaskId} không đọc lại được — chạy lại từ đầu.", taskId);
        }

        await db.AgentRunCheckpoints.Where(x => x.TaskId == taskId).ExecuteDeleteAsync(cancellationToken);
        return null;
    }

    /// <summary>Bỏ checkpoint — task đã hoàn tất, hoặc người dùng chọn chạy lại từ đầu.</summary>
    public virtual async Task DeleteAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AgentRunCheckpoints.Where(x => x.TaskId == taskId).ExecuteDeleteAsync(cancellationToken);
    }

    // AIJsonUtilities.DefaultOptions biết các kiểu AIContent đa hình (text, lời gọi tool, kết quả tool, suy
    // luận ẩn…) — cùng bộ serializer mà chính Microsoft.Extensions.AI dùng để lưu hội thoại.
    public static string Serialize(IReadOnlyList<ChatMessage> messages) =>
        JsonSerializer.Serialize(messages, AIJsonUtilities.DefaultOptions);

    public static List<ChatMessage> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<ChatMessage>>(json, AIJsonUtilities.DefaultOptions) ?? new List<ChatMessage>();

    /// <summary>
    /// Mọi lời gọi tool trong hội thoại phải có kết quả đi kèm, nếu không endpoint từ chối cả request. Lời gọi
    /// còn treo xuất hiện khi lượt trước bị ngắt giữa lúc tool đang chạy; điền cho nó
    /// <see cref="InterruptedToolResult"/> ngay sau lượt assistant đã gọi nó.
    /// </summary>
    public static List<ChatMessage> ResolveInterruptedToolCalls(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count + 1);
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            result.Add(message);

            var callIds = message.Contents.OfType<FunctionCallContent>().Select(c => c.CallId).ToList();
            if (callIds.Count == 0)
                continue;

            // Kết quả của một lượt gọi tool nằm ở các message Tool đứng NGAY SAU nó.
            var answered = new HashSet<string>(StringComparer.Ordinal);
            var next = i + 1;
            for (; next < messages.Count && messages[next].Role == ChatRole.Tool; next++)
            {
                result.Add(messages[next]);
                foreach (var functionResult in messages[next].Contents.OfType<FunctionResultContent>())
                    answered.Add(functionResult.CallId);
            }
            i = next - 1;

            var pending = callIds.Where(id => !answered.Contains(id)).ToList();
            if (pending.Count > 0)
                result.Add(new ChatMessage(ChatRole.Tool,
                    pending.Select(id => (AIContent)new FunctionResultContent(id, InterruptedToolResult)).ToList()));
        }

        return result;
    }
}
