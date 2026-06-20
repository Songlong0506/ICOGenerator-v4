using System.Threading.Channels;

namespace ICOGenerator.Services.Workflows;

public record WorkflowProgressEvent(long Seq, DateTime At, string Kind, string Message, string? Detail);

/// <summary>Live subscription to one workflow run's progress (đọc bằng SSE). Dispose để hủy đăng ký.</summary>
public interface IWorkflowProgressSubscription : IDisposable
{
    ChannelReader<WorkflowProgressEvent> Reader { get; }
}

/// <summary>
/// Thu thập sự kiện tiến độ của một workflow run. Vừa giữ một bộ đệm để UI poll/replay (qua
/// <see cref="GetEvents"/>), vừa đẩy realtime tới các subscriber đang mở (qua <see cref="Subscribe"/>)
/// để stream bằng SSE — bao gồm cả token "suy nghĩ" của model qua <see cref="ReportToken"/>.
/// </summary>
public interface IWorkflowProgressReporter
{
    void Report(Guid runId, string kind, string message, string? detail = null);

    /// <summary>
    /// Đẩy một token (delta nội dung do model sinh ra) tới các subscriber đang mở. Token là dữ liệu
    /// "live-only": KHÔNG được lưu vào bộ đệm replay (Seq = 0) nên không tốn quota sự kiện và không
    /// phát lại khi client kết nối lại — đúng bản chất "đang gõ".
    /// </summary>
    void ReportToken(Guid runId, string token);

    IReadOnlyList<WorkflowProgressEvent> GetEvents(Guid runId, long afterSeq = 0);

    /// <summary>Mở một kênh nhận realtime cho run. Gọi trước khi đọc backlog để không bỏ lỡ sự kiện nào.</summary>
    IWorkflowProgressSubscription Subscribe(Guid runId);
}

public class WorkflowProgressReporter : IWorkflowProgressReporter
{
    private const int MaxEventsPerRun = 300;
    private const int MaxTrackedRuns = 50;
    private const int MaxDetailLength = 600;

    // Mỗi subscriber có một kênh có giới hạn: nếu client quá chậm thì bỏ token cũ nhất thay vì
    // chặn worker (DropOldest + TryWrite không bao giờ block). Milestone hiếm hơn token rất nhiều
    // nên gần như không bị rớt; nếu rớt, client vẫn nhận lại được qua backlog khi reconnect (afterSeq).
    private const int SubscriberBufferSize = 1024;

    // The token marker: live-only events carry Seq 0 so they are never stored or replayed.
    public const long TokenSeq = 0;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, List<WorkflowProgressEvent>> _events = new();
    private readonly Dictionary<Guid, List<Channel<WorkflowProgressEvent>>> _subscribers = new();
    private readonly Queue<Guid> _runOrder = new();
    private long _seq;

    public void Report(Guid runId, string kind, string message, string? detail = null)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(runId, out var list))
            {
                list = new List<WorkflowProgressEvent>();
                _events[runId] = list;
                _runOrder.Enqueue(runId);

                while (_runOrder.Count > MaxTrackedRuns)
                    _events.Remove(_runOrder.Dequeue());
            }

            var ev = new WorkflowProgressEvent(++_seq, DateTime.UtcNow, kind, message, Truncate(detail));
            list.Add(ev);

            if (list.Count > MaxEventsPerRun)
                list.RemoveAt(0);

            Publish(runId, ev);
        }
    }

    public void ReportToken(Guid runId, string token)
    {
        if (string.IsNullOrEmpty(token))
            return;

        lock (_gate)
        {
            // Skip the work entirely when nobody is watching — token volume is high.
            if (!_subscribers.ContainsKey(runId))
                return;

            Publish(runId, new WorkflowProgressEvent(TokenSeq, DateTime.UtcNow, "token", token, null));
        }
    }

    public IReadOnlyList<WorkflowProgressEvent> GetEvents(Guid runId, long afterSeq = 0)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(runId, out var list))
                return Array.Empty<WorkflowProgressEvent>();

            return list.Where(x => x.Seq > afterSeq).ToList();
        }
    }

    public IWorkflowProgressSubscription Subscribe(Guid runId)
    {
        var channel = Channel.CreateBounded<WorkflowProgressEvent>(new BoundedChannelOptions(SubscriberBufferSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        lock (_gate)
        {
            if (!_subscribers.TryGetValue(runId, out var list))
            {
                list = new List<Channel<WorkflowProgressEvent>>();
                _subscribers[runId] = list;
            }

            list.Add(channel);
        }

        return new Subscription(this, runId, channel);
    }

    // Fan a single event out to every open subscriber for the run. Must be called under _gate.
    // TryWrite is non-blocking (bounded + DropOldest), so holding the lock here is safe and cheap.
    private void Publish(Guid runId, WorkflowProgressEvent ev)
    {
        if (!_subscribers.TryGetValue(runId, out var channels))
            return;

        foreach (var channel in channels)
            channel.Writer.TryWrite(ev);
    }

    private void Unsubscribe(Guid runId, Channel<WorkflowProgressEvent> channel)
    {
        lock (_gate)
        {
            if (_subscribers.TryGetValue(runId, out var list))
            {
                list.Remove(channel);
                if (list.Count == 0)
                    _subscribers.Remove(runId);
            }
        }

        // Let a blocked reader (the SSE loop) wake up and finish cleanly.
        channel.Writer.TryComplete();
    }

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();
        return text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "…";
    }

    private sealed class Subscription : IWorkflowProgressSubscription
    {
        private readonly WorkflowProgressReporter _owner;
        private readonly Guid _runId;
        private readonly Channel<WorkflowProgressEvent> _channel;
        private bool _disposed;

        public Subscription(WorkflowProgressReporter owner, Guid runId, Channel<WorkflowProgressEvent> channel)
        {
            _owner = owner;
            _runId = runId;
            _channel = channel;
        }

        public ChannelReader<WorkflowProgressEvent> Reader => _channel.Reader;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _owner.Unsubscribe(_runId, _channel);
        }
    }
}
