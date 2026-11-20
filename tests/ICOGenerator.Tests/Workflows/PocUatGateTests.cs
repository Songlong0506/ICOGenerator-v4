using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Workflows;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

/// <summary>
/// Cổng kịch bản nghiệm thu của bước POC: bản demo chỉ tới cổng duyệt khi máy đã chạy hết bộ UAT và tất cả
/// đều đạt; còn gãy thì tự sửa tới trần rồi mới đi tiếp (nói rõ là còn gãy). Audit và vòng sửa là fake —
/// thứ được giữ ở đây là luật điều phối, không phải trình duyệt hay model.
/// </summary>
public class PocUatGateTests
{
    private static PocVerificationSummary Summary(int total, params string[] failures) => new()
    {
        RuntimeRan = true,
        UatTotal = total,
        UatIssues = failures.ToList()
    };

    private static readonly Action<string, string, string?> NoReport = (_, _, _) => { };

    [Fact]
    public void Evaluate_AllScenariosPassing_IsPassed()
    {
        var outcome = PocUatGate.Evaluate(Summary(3));

        Assert.Equal(PocUatGateStatus.Passed, outcome.Status);
        Assert.Equal(3, outcome.UatTotal);
        Assert.Empty(outcome.RemainingFailures);
    }

    [Fact]
    public void Evaluate_AFailingScenario_IsStillFailing()
    {
        var outcome = PocUatGate.Evaluate(Summary(3, "Kịch bản A chưa đạt"));

        Assert.Equal(PocUatGateStatus.StillFailing, outcome.Status);
        Assert.Equal(new[] { "Kịch bản A chưa đạt" }, outcome.RemainingFailures);
    }

    // Không đo được thì KHÔNG được mở vòng sửa: không có Chromium (CI, máy dev) hay không có bộ UAT mà vẫn
    // giao agent "sửa cho đạt" là đốt token để sửa một thứ không ai chấm được.
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    public void Evaluate_WhatCannotBeMeasured_IsUnverified(bool runtimeRan, int total)
    {
        var summary = new PocVerificationSummary { RuntimeRan = runtimeRan, UatTotal = total, UatIssues = { "x" } };

        Assert.Equal(PocUatGateStatus.Unverified, PocUatGate.Evaluate(summary).Status);
    }

    [Fact]
    public void Evaluate_NoDemo_IsUnverified()
    {
        Assert.Equal(PocUatGateStatus.Unverified, PocUatGate.Evaluate(null).Status);
    }

    [Fact]
    public async Task Enforce_PassingOnTheFirstAudit_RunsNoFixRound()
    {
        var fixes = 0;

        var outcome = await PocUatGate.EnforceAsync(
            _ => Task.FromResult<PocVerificationSummary?>(Summary(2)),
            (_, _, _) => { fixes++; return Task.CompletedTask; },
            maxRounds: 2, NoReport, CancellationToken.None);

        Assert.Equal(PocUatGateStatus.Passed, outcome.Status);
        Assert.Equal(0, outcome.RoundsUsed);
        Assert.Equal(0, fixes);
    }

    [Fact]
    public async Task Enforce_FixesUntilClean_ThenPasses()
    {
        var audits = new Queue<PocVerificationSummary>(new[]
        {
            Summary(3, "A gãy", "B gãy"),
            Summary(3, "B gãy"),
            Summary(3)
        });
        var seenFailures = new List<IReadOnlyList<string>>();

        var outcome = await PocUatGate.EnforceAsync(
            _ => Task.FromResult<PocVerificationSummary?>(audits.Dequeue()),
            (_, failures, _) => { seenFailures.Add(failures); return Task.CompletedTask; },
            maxRounds: 2, NoReport, CancellationToken.None);

        Assert.Equal(PocUatGateStatus.Passed, outcome.Status);
        Assert.Equal(2, outcome.RoundsUsed);
        // Mỗi vòng sửa nhận đúng danh sách của lần kiểm NGAY TRƯỚC nó, không phải danh sách ban đầu.
        Assert.Equal(new[] { "A gãy", "B gãy" }, seenFailures[0]);
        Assert.Equal(new[] { "B gãy" }, seenFailures[1]);
    }

    // Hết ngạch thì dừng và nói thật, không lặp mãi: lỗi không hội tụ là chuyện thường của model.
    [Fact]
    public async Task Enforce_StopsAtTheCeiling_AndReportsWhatIsStillBroken()
    {
        var fixes = 0;
        var errors = new List<string>();

        var outcome = await PocUatGate.EnforceAsync(
            _ => Task.FromResult<PocVerificationSummary?>(Summary(2, "A gãy")),
            (_, _, _) => { fixes++; return Task.CompletedTask; },
            maxRounds: 2,
            (kind, message, _) => { if (kind == "error") errors.Add(message); },
            CancellationToken.None);

        Assert.Equal(PocUatGateStatus.StillFailing, outcome.Status);
        Assert.Equal(2, fixes);
        Assert.Equal(2, outcome.RoundsUsed);
        Assert.Equal(new[] { "A gãy" }, outcome.RemainingFailures);
        Assert.Single(errors);
    }

    // Cổng hỏng KHÁC demo hỏng: lỗi hạ tầng của chính cổng không được giữ bản demo lại.
    [Fact]
    public async Task Enforce_AuditThatThrows_IsUnverified_NotAFailure()
    {
        var outcome = await PocUatGate.EnforceAsync(
            _ => throw new InvalidOperationException("Chromium không mở được"),
            (_, _, _) => Task.CompletedTask,
            maxRounds: 2, NoReport, CancellationToken.None);

        Assert.Equal(PocUatGateStatus.Unverified, outcome.Status);
        Assert.Contains("Chromium", outcome.SkipReason);
    }

    // Vòng sửa hỏng: không thử thêm vòng nào, nhưng vẫn chấm lại MỘT lần — bản vá có thể đã kịp ghi xuống đĩa.
    [Fact]
    public async Task Enforce_FixRoundThatThrows_StopsFixing_ButReAuditsOnce()
    {
        var audits = 0;
        var fixes = 0;

        var outcome = await PocUatGate.EnforceAsync(
            _ => { audits++; return Task.FromResult<PocVerificationSummary?>(Summary(2, "A gãy")); },
            (_, _, _) => { fixes++; throw new InvalidOperationException("model lỗi"); },
            maxRounds: 3, NoReport, CancellationToken.None);

        Assert.Equal(PocUatGateStatus.StillFailing, outcome.Status);
        Assert.Equal(1, fixes);
        Assert.Equal(2, audits);
    }

    [Fact]
    public async Task Enforce_Cancellation_IsNotSwallowed()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PocUatGate.EnforceAsync(
            _ => Task.FromResult<PocVerificationSummary?>(Summary(2, "A gãy")),
            (_, _, _) => throw new OperationCanceledException(),
            maxRounds: 2, NoReport, CancellationToken.None));
    }

    [Fact]
    public void HandoffSection_ListsWhatIsStillBroken_AndEachFixRound()
    {
        var outcome = new PocUatGateOutcome(PocUatGateStatus.StillFailing, 2, 3, new[] { "A gãy" });

        var section = PocUatGate.BuildHandoffSection(outcome, new[] { "- A: đã nối nút Gửi", "" });

        Assert.NotNull(section);
        Assert.Contains("A gãy", section);
        Assert.Contains("đã nối nút Gửi", section);
    }

    [Fact]
    public void HandoffSection_Unverified_AddsNothing()
    {
        var outcome = new PocUatGateOutcome(PocUatGateStatus.Unverified, 0, 0, Array.Empty<string>(), "no browser");

        Assert.Null(PocUatGate.BuildHandoffSection(outcome, Array.Empty<string>()));
    }

    // File prompt của vòng sửa phải tồn tại THẬT: builder chỉ tra đường dẫn, thiếu file thì lỗi chỉ nổ ra
    // lúc cổng mở vòng sửa đầu tiên — tức sau khi đã trả tiền cho cả lượt dựng POC.
    [Fact]
    public void FixPrompt_File_Exists_And_Carries_Its_Placeholders()
    {
        var text = ICOGenerator.Tests.Requirements.PromptFixture.Read("Developer/poc-uat-fix.v1.md");

        Assert.Contains("{{previous_output}}", text);
        Assert.Contains("{{failures}}", text);
        Assert.Contains("FixPocAndAudit", text);
    }
}
