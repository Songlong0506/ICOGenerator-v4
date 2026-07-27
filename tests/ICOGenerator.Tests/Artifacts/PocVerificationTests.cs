using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Bản chụp kết quả vòng tự kiểm cuối của POC (poc-verification.json). Chốt: (1) Save/TryLoad round-trip
// đủ trường; (2) TryLoad fail-open với file thiếu/hỏng — trang review chỉ việc ẩn panel, không vỡ.
public class PocVerificationTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "poc-verif-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task SaveAsync_ThenTryLoad_RoundTripsSummary()
    {
        var summary = new PocVerificationSummary
        {
            CheckedAtUtc = new DateTime(2026, 7, 20, 8, 30, 0, DateTimeKind.Utc),
            SpecScreens = 7,
            CoveredScreens = 6,
            OpenIssues = new List<string> { "Spec screen 'Báo cáo' has no matching section." },
            SelfTestResults = new List<string> { "PASS BR-1 — tổng trọng số 100% hợp lệ", "FAIL BR-2 — kỳ vọng 81, thực tế 78" },
            ScenarioResults = new List<string> { "PASS Gửi và duyệt đơn" },
            WorkedExamplesTotal = 2,
            WorkedExampleIssues = new List<string> { "Worked example WE-1 SAI." },
            RuntimeRan = true,
            VisualRan = true,
            VisualWarnings = new List<string> { "Thẻ thống kê lệch chiều cao." }
        };

        await PocVerification.SaveAsync(_workspace, summary);
        var loaded = PocVerification.TryLoad(_workspace);

        Assert.NotNull(loaded);
        Assert.Equal(summary.CheckedAtUtc, loaded!.CheckedAtUtc);
        Assert.Equal(7, loaded.SpecScreens);
        Assert.Equal(6, loaded.CoveredScreens);
        Assert.Single(loaded.OpenIssues);
        Assert.Equal(2, loaded.SelfTestResults.Count);
        Assert.Single(loaded.ScenarioResults);
        Assert.Equal(2, loaded.WorkedExamplesTotal);
        Assert.Single(loaded.WorkedExampleIssues);
        Assert.True(loaded.RuntimeRan);
        Assert.True(loaded.VisualRan);
        Assert.Single(loaded.VisualWarnings);
    }

    [Fact]
    public async Task SaveAsync_Overwrites_SoFileAlwaysHoldsTheLatestRound()
    {
        await PocVerification.SaveAsync(_workspace, new PocVerificationSummary { OpenIssues = new List<string> { "còn issue" } });
        await PocVerification.SaveAsync(_workspace, new PocVerificationSummary { RuntimeRan = true });

        var loaded = PocVerification.TryLoad(_workspace);
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.OpenIssues);
        Assert.True(loaded.RuntimeRan);
    }

    [Fact]
    public void TryLoad_MissingOrCorruptFile_ReturnsNull()
    {
        Assert.Null(PocVerification.TryLoad(_workspace));

        Directory.CreateDirectory(Path.GetDirectoryName(PocVerification.GetPath(_workspace))!);
        File.WriteAllText(PocVerification.GetPath(_workspace), "{ khong-phai-json");
        Assert.Null(PocVerification.TryLoad(_workspace));
    }

    // ==== Lịch sử vòng kiểm + phát hiện HỒI QUY ====
    // Bản chụp "chỉ vòng cuối" không thấy được lớp lỗi nguy hiểm nhất của vòng sửa: agent sửa issue rồi
    // làm gãy thứ đang chạy. So vòng này với vòng liền trước mới bắt được.

    [Fact]
    public async Task SaveAsync_PushesPreviousRoundIntoHistory()
    {
        await PocVerification.SaveAsync(_workspace, new PocVerificationSummary { SpecScreens = 1 });
        await PocVerification.SaveAsync(_workspace, new PocVerificationSummary { SpecScreens = 2 });
        await PocVerification.SaveAsync(_workspace, new PocVerificationSummary { SpecScreens = 3 });

        Assert.Equal(3, PocVerification.TryLoad(_workspace)!.SpecScreens);

        var history = PocVerification.TryLoadHistory(_workspace);
        Assert.Equal(new[] { 1, 2 }, history.Select(h => h.SpecScreens).ToArray());
    }

    [Fact]
    public void DetectRegressions_RulePassedBeforeButFailsNow_IsReported()
    {
        var previous = new PocVerificationSummary
        {
            SelfTestResults = new List<string> { "PASS BR-1 — ok", "FAIL BR-2 — kỳ vọng 81, thực tế 78" }
        };

        var regressions = PocVerification.DetectRegressions(
            previous,
            new[] { "FAIL BR-1 — kỳ vọng 100%, thực tế 90%", "PASS BR-2 — ok" },
            Array.Empty<string>());

        Assert.Single(regressions);
        Assert.Contains("BR-1", regressions[0]);
    }

    [Fact]
    public void DetectRegressions_AssertionDeletedInsteadOfFixed_IsAlsoReported()
    {
        // Xoá assertion để "hết fail" là đúng cái mẹo mà prompt POC cấm — phải bắt được.
        var previous = new PocVerificationSummary { SelfTestResults = new List<string> { "PASS BR-3 — ok" } };

        var regressions = PocVerification.DetectRegressions(previous, Array.Empty<string>(), Array.Empty<string>());

        Assert.Single(regressions);
        Assert.Contains("BR-3", regressions[0]);
        Assert.Contains("KHÔNG CÒN được kiểm", regressions[0]);
    }

    [Fact]
    public void DetectRegressions_NewlyFailingItem_IsNotARegression()
    {
        // Mục MỚI xuất hiện mà fail đã nằm trong ISSUES thường — báo lại ở đây chỉ gây nhiễu.
        var previous = new PocVerificationSummary { SelfTestResults = new List<string> { "PASS BR-1 — ok" } };

        var regressions = PocVerification.DetectRegressions(
            previous, new[] { "PASS BR-1 — ok", "FAIL BR-9 — mới thêm" }, Array.Empty<string>());

        Assert.Empty(regressions);
    }

    [Fact]
    public void DetectRegressions_ScenarioRegression_IsReportedToo()
    {
        var previous = new PocVerificationSummary
        {
            ScenarioResults = new List<string> { "PASS Gửi và duyệt đơn — ok" }
        };

        var regressions = PocVerification.DetectRegressions(
            previous, Array.Empty<string>(), new[] { "FAIL Gửi và duyệt đơn — đơn không rời danh sách chờ" });

        Assert.Single(regressions);
        Assert.Contains("Gửi và duyệt đơn", regressions[0]);
    }

    [Fact]
    public void DetectRegressions_NoPreviousRound_ReportsNothing()
        => Assert.Empty(PocVerification.DetectRegressions(null, new[] { "FAIL BR-1 — x" }, Array.Empty<string>()));

    [Fact]
    public async Task Reset_ClearsLatestAndHistory_SoARebuiltPocStartsClean()
    {
        await PocVerification.SaveAsync(_workspace, new PocVerificationSummary { SpecScreens = 1 });
        await PocVerification.SaveAsync(_workspace, new PocVerificationSummary { SpecScreens = 2 });

        PocVerification.Reset(_workspace);

        Assert.Null(PocVerification.TryLoad(_workspace));
        Assert.Empty(PocVerification.TryLoadHistory(_workspace));
    }
}
