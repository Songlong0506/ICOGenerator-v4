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
}
