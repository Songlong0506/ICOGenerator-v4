using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

/// <summary>
/// Stream tiến độ của một workflow run theo thời gian thực cho SSE: phát lại backlog (sau
/// <c>afterSeq</c>) rồi đẩy tiếp các sự kiện/token live cho tới khi run kết thúc hoặc client ngắt.
/// Trả về một phần tử <c>null</c> mỗi khi im lặng quá lâu để controller gửi heartbeat giữ kết nối.
/// </summary>
public class StreamWorkflowProgressQuery
{
    // Phát heartbeat khi không có sự kiện nào trong khoảng này — giữ kết nối qua proxy có idle-timeout.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly AppDbContext _db;
    private readonly IWorkflowProgressReporter _progress;

    public StreamWorkflowProgressQuery(AppDbContext db, IWorkflowProgressReporter progress)
    {
        _db = db;
        _progress = progress;
    }

    public async IAsyncEnumerable<WorkflowProgressEventVm?> ExecuteAsync(
        Guid projectId, Guid runId, long afterSeq,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var status = await _db.WorkflowRuns
            .AsNoTracking()
            .Where(x => x.Id == runId && x.ProjectId == projectId)
            .Select(x => (WorkflowRunStatus?)x.Status)
            .FirstOrDefaultAsync(cancellationToken);

        // Run không thuộc project (hoặc không tồn tại): không stream gì.
        if (status is null)
            yield break;

        // Subscribe TRƯỚC khi đọc backlog để không lọt sự kiện phát ra ngay sau snapshot.
        using var subscription = _progress.Subscribe(runId);

        var lastSeq = afterSeq;
        foreach (var ev in _progress.GetEvents(runId, afterSeq))
        {
            yield return WorkflowProgressEventVm.From(ev);
            lastSeq = ev.Seq;
        }

        // Run đã kết thúc hẳn lúc kết nối → đã phát lại đủ backlog, đóng luôn (không có gì live nữa).
        if (IsTerminal(status.Value))
            yield break;

        var reader = subscription.Reader;
        while (!cancellationToken.IsCancellationRequested)
        {
            var (kind, ev) = await NextAsync(reader, cancellationToken);

            if (kind == NextKind.Closed)
                yield break;

            if (kind == NextKind.Heartbeat)
            {
                yield return null;
                continue;
            }

            // Bỏ qua milestone đã phát trong backlog (chống trùng khi reconnect); token (Seq 0) luôn qua.
            if (ev!.Seq != WorkflowProgressReporter.TokenSeq)
            {
                if (ev.Seq <= lastSeq)
                    continue;
                lastSeq = ev.Seq;
            }

            yield return WorkflowProgressEventVm.From(ev);

            // "completed"/"error" có thể là kết thúc hẳn hoặc chỉ là cổng chờ duyệt (sẽ chạy tiếp cùng
            // run). Soi lại trạng thái thật trong DB: chỉ đóng stream khi run đã thực sự terminal.
            if ((ev.Kind == "completed" || ev.Kind == "error") && await IsRunTerminalAsync(runId, cancellationToken))
                yield break;
        }
    }

    private enum NextKind { Event, Heartbeat, Closed }

    // Lấy sự kiện kế tiếp, hoặc báo Heartbeat khi quá HeartbeatInterval, hoặc Closed khi kênh đóng/hủy.
    // Tách riêng để vòng lặp ngoài không phải đặt "yield return" trong try/catch (C# không cho phép).
    private static async Task<(NextKind Kind, WorkflowProgressEvent? Event)> NextAsync(
        ChannelReader<WorkflowProgressEvent> reader, CancellationToken cancellationToken)
    {
        if (reader.TryRead(out var ready))
            return (NextKind.Event, ready);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        heartbeatCts.CancelAfter(HeartbeatInterval);

        try
        {
            if (!await reader.WaitToReadAsync(heartbeatCts.Token))
                return (NextKind.Closed, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (NextKind.Heartbeat, null);
        }
        catch (OperationCanceledException)
        {
            return (NextKind.Closed, null);
        }

        return reader.TryRead(out var ev) ? (NextKind.Event, ev) : (NextKind.Heartbeat, null);
    }

    private async Task<bool> IsRunTerminalAsync(Guid runId, CancellationToken cancellationToken)
    {
        var status = await _db.WorkflowRuns
            .AsNoTracking()
            .Where(x => x.Id == runId)
            .Select(x => (WorkflowRunStatus?)x.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status is null || IsTerminal(status.Value);
    }

    private static bool IsTerminal(WorkflowRunStatus status) =>
        status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Canceled;
}
