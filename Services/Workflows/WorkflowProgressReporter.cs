namespace ICOGenerator.Services.Workflows;

public record WorkflowProgressEvent(long Seq, DateTime At, string Kind, string Message, string? Detail);

/// <summary>
/// Thu thập các sự kiện tiến độ (suy nghĩ, gọi tool, kết quả…) của một workflow run
/// để UI có thể poll và hiển thị live giống Claude/ChatGPT.
/// </summary>
public interface IWorkflowProgressReporter
{
    void Report(Guid runId, string kind, string message, string? detail = null);
    IReadOnlyList<WorkflowProgressEvent> GetEvents(Guid runId, long afterSeq = 0);
}

public class WorkflowProgressReporter : IWorkflowProgressReporter
{
    private const int MaxEventsPerRun = 300;
    private const int MaxTrackedRuns = 50;
    private const int MaxDetailLength = 600;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, List<WorkflowProgressEvent>> _events = new();
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

            list.Add(new WorkflowProgressEvent(++_seq, DateTime.UtcNow, kind, message, Truncate(detail)));

            if (list.Count > MaxEventsPerRun)
                list.RemoveAt(0);
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

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();
        return text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "…";
    }
}
