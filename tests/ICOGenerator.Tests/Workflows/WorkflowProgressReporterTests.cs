using ICOGenerator.Services.Workflows;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

public class WorkflowProgressReporterTests
{
    [Fact]
    public void Subscribe_ReceivesMilestoneEventsLive()
    {
        var reporter = new WorkflowProgressReporter();
        var runId = Guid.NewGuid();

        using var sub = reporter.Subscribe(runId);
        reporter.Report(runId, "start", "Bắt đầu", "chi tiết");

        Assert.True(sub.Reader.TryRead(out var ev));
        Assert.NotNull(ev);
        Assert.Equal("start", ev!.Kind);
        Assert.Equal("Bắt đầu", ev.Message);
        Assert.Equal("chi tiết", ev.Detail);
        Assert.True(ev.Seq > 0);
    }

    [Fact]
    public void ReportToken_DeliveredLive_ButNotStoredInBacklog()
    {
        var reporter = new WorkflowProgressReporter();
        var runId = Guid.NewGuid();

        using var sub = reporter.Subscribe(runId);
        reporter.ReportToken(runId, "Hel");
        reporter.ReportToken(runId, "lo");

        Assert.True(sub.Reader.TryRead(out var first));
        Assert.Equal("token", first!.Kind);
        Assert.Equal("Hel", first.Message);
        Assert.Equal(WorkflowProgressReporter.TokenSeq, first.Seq);

        Assert.True(sub.Reader.TryRead(out var second));
        Assert.Equal("lo", second!.Message);

        // Tokens are live-only: they must never appear in the replay backlog.
        Assert.Empty(reporter.GetEvents(runId));
    }

    [Fact]
    public void ReportToken_WithNoSubscriber_IsNoOp()
    {
        var reporter = new WorkflowProgressReporter();
        var runId = Guid.NewGuid();

        reporter.ReportToken(runId, "ignored");

        Assert.Empty(reporter.GetEvents(runId));
    }

    [Fact]
    public void GetEvents_ReplaysBacklogAfterSeq()
    {
        var reporter = new WorkflowProgressReporter();
        var runId = Guid.NewGuid();

        reporter.Report(runId, "start", "one");
        reporter.Report(runId, "thinking", "two");

        var all = reporter.GetEvents(runId);
        Assert.Equal(2, all.Count);

        // Cursor past the first event returns only what came after it.
        var rest = reporter.GetEvents(runId, all[0].Seq);
        Assert.Single(rest);
        Assert.Equal("two", rest[0].Message);
    }

    [Fact]
    public void Dispose_StopsDelivery()
    {
        var reporter = new WorkflowProgressReporter();
        var runId = Guid.NewGuid();

        var sub = reporter.Subscribe(runId);
        sub.Dispose();

        // After unsubscribe the channel is completed; nothing new is delivered.
        reporter.Report(runId, "start", "after-dispose");
        Assert.False(sub.Reader.TryRead(out _));
    }

    [Fact]
    public void Report_FansOutToAllSubscribers()
    {
        var reporter = new WorkflowProgressReporter();
        var runId = Guid.NewGuid();

        using var a = reporter.Subscribe(runId);
        using var b = reporter.Subscribe(runId);

        reporter.Report(runId, "completed", "done");

        Assert.True(a.Reader.TryRead(out var evA));
        Assert.True(b.Reader.TryRead(out var evB));
        Assert.Equal("done", evA!.Message);
        Assert.Equal("done", evB!.Message);
    }
}
