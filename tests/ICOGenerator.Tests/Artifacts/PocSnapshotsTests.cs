using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Mỗi vòng "Yêu cầu chỉnh sửa" ghi đè thẳng lên poc-demo.html. Không chụp lại thì bản người nghiệm thu
// vừa xem biến mất, và họ chỉ còn bản bàn giao bằng chữ của agent để tin. Các test này giữ đúng hai điều
// khiến tính năng có ích: đánh số vòng liên tục, và diff nói được bằng ngôn ngữ màn hình.
public class PocSnapshotsTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "poc-snap-" + Guid.NewGuid().ToString("N"));

    private string MockupPath => Path.Combine(_workspace, "04_Implementation", "poc-demo.html");

    public PocSnapshotsTests() => Directory.CreateDirectory(Path.GetDirectoryName(MockupPath)!);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // Dọn dẹp best-effort — không được làm fail test.
        }
    }

    private void WriteMockup(params string[] screens) =>
        File.WriteAllText(MockupPath, Html(screens));

    private static string Html(params string[] screens) =>
        "<html><body>"
        + string.Join("", screens.Select(s => $"<section class=\"page-view\" data-view=\"{s}\">nội dung</section>"))
        + "</body></html>";

    [Fact]
    public void TryCapture_NumbersRoundsSequentially()
    {
        WriteMockup("Đăng nhập");
        var first = PocSnapshots.TryCapture(_workspace, MockupPath);

        WriteMockup("Đăng nhập", "Duyệt đơn");
        var second = PocSnapshots.TryCapture(_workspace, MockupPath);

        Assert.Equal(1, first!.Version);
        Assert.Equal(2, second!.Version);
        Assert.Equal([1, 2], PocSnapshots.List(_workspace).Select(x => x.Version));
    }

    [Fact]
    public void TryCapture_KeepsTheContentOfThatRound()
    {
        WriteMockup("Đăng nhập");
        PocSnapshots.TryCapture(_workspace, MockupPath);

        // Vòng sửa ghi đè bản hiện tại — bản chụp phải KHÔNG đổi theo.
        WriteMockup("Đăng nhập", "Duyệt đơn");

        var v1 = PocSnapshots.TryReadContent(PocSnapshots.TryGetPath(_workspace, 1)!);
        Assert.DoesNotContain("Duyệt đơn", v1);
    }

    [Fact]
    public void TryCapture_WithoutMockupFile_ReturnsNull()
    {
        Assert.Null(PocSnapshots.TryCapture(_workspace, MockupPath));
        Assert.Empty(PocSnapshots.List(_workspace));
    }

    [Fact]
    public void Reset_DropsHistory_SoARebuiltPocIsNotComparedToADemoThatNoLongerExists()
    {
        WriteMockup("Đăng nhập");
        PocSnapshots.TryCapture(_workspace, MockupPath);

        PocSnapshots.Reset(_workspace);

        Assert.Empty(PocSnapshots.List(_workspace));
        // Sau reset, vòng kế tiếp bắt đầu lại từ 1.
        WriteMockup("Đăng nhập");
        Assert.Equal(1, PocSnapshots.TryCapture(_workspace, MockupPath)!.Version);
    }

    [Fact]
    public void TryGetPath_UnknownVersion_ReturnsNull()
    {
        WriteMockup("Đăng nhập");
        PocSnapshots.TryCapture(_workspace, MockupPath);

        Assert.Null(PocSnapshots.TryGetPath(_workspace, 99));
    }

    [Fact]
    public void Diff_ReportsScreensAddedAndRemoved()
    {
        var diff = PocSnapshots.Diff(
            Html("Đăng nhập", "Danh sách đơn"),
            Html("Đăng nhập", "Duyệt đơn"));

        Assert.Equal(["Duyệt đơn"], diff.AddedScreens);
        Assert.Equal(["Danh sách đơn"], diff.RemovedScreens);
        Assert.True(diff.HasChanges);
    }

    // Khác biệt hoa/thường hay khoảng trắng của cùng một nhãn không được biến thành "thêm rồi bỏ" —
    // người review sẽ đi tìm một thay đổi không có thật.
    [Fact]
    public void Diff_NormalizesLabels()
    {
        var diff = PocSnapshots.Diff(Html("Duyệt đơn"), Html("duyệt  đơn"));

        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void Diff_MissingSide_IsEmpty()
    {
        Assert.False(PocSnapshots.Diff(null, Html("A")).HasChanges);
        Assert.False(PocSnapshots.Diff(Html("A"), null).HasChanges);
    }
}
