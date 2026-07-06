using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Diff theo dòng cho lịch sử tài liệu: dòng chung giữ Same, dòng chỉ có ở bản cũ là Removed, chỉ có ở
// bản mới là Added; thứ tự phát ra phải đọc được như một unified diff (cũ trước, mới sau tại cùng vị trí).
public class DocumentDiffServiceTests
{
    private readonly DocumentDiffService _diff = new();

    [Fact]
    public void Diff_IdenticalTexts_AllSame()
    {
        var lines = _diff.Diff("a\nb\nc", "a\nb\nc");

        Assert.All(lines, l => Assert.Equal(DiffLineKind.Same, l.Kind));
        Assert.Equal(new[] { "a", "b", "c" }, lines.Select(l => l.Text));
    }

    [Fact]
    public void Diff_EmptyOld_AllAdded()
    {
        var lines = _diff.Diff(null, "x\ny");

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(DiffLineKind.Added, l.Kind));
    }

    [Fact]
    public void Diff_EmptyNew_AllRemoved()
    {
        var lines = _diff.Diff("x\ny", "");

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(DiffLineKind.Removed, l.Kind));
    }

    [Fact]
    public void Diff_ChangedMiddleLine_MarksRemovedThenAdded()
    {
        var lines = _diff.Diff("mở đầu\ndòng cũ\nkết thúc", "mở đầu\ndòng mới\nkết thúc");

        Assert.Equal(4, lines.Count);
        Assert.Equal((DiffLineKind.Same, "mở đầu"), (lines[0].Kind, lines[0].Text));
        Assert.Equal((DiffLineKind.Removed, "dòng cũ"), (lines[1].Kind, lines[1].Text));
        Assert.Equal((DiffLineKind.Added, "dòng mới"), (lines[2].Kind, lines[2].Text));
        Assert.Equal((DiffLineKind.Same, "kết thúc"), (lines[3].Kind, lines[3].Text));
    }

    [Fact]
    public void Diff_InsertionOnly_KeepsSurroundingLinesSame()
    {
        var lines = _diff.Diff("a\nc", "a\nb\nc");

        Assert.Equal(3, lines.Count);
        Assert.Equal(DiffLineKind.Same, lines[0].Kind);
        Assert.Equal((DiffLineKind.Added, "b"), (lines[1].Kind, lines[1].Text));
        Assert.Equal(DiffLineKind.Same, lines[2].Kind);
    }

    [Fact]
    public void Diff_NormalizesCrLf()
    {
        var lines = _diff.Diff("a\r\nb", "a\nb");

        Assert.All(lines, l => Assert.Equal(DiffLineKind.Same, l.Kind));
    }

    // Vùng đổi vượt trần DP ⇒ fallback "thay cả khối": vẫn đủ Removed/Added, không nổ bộ nhớ.
    [Fact]
    public void Diff_HugeChangedRegion_FallsBackToWholesaleReplace()
    {
        var oldText = string.Join("\n", Enumerable.Range(0, 3000).Select(i => "cũ " + i));
        var newText = string.Join("\n", Enumerable.Range(0, 3000).Select(i => "mới " + i));

        var lines = _diff.Diff(oldText, newText);

        Assert.Equal(3000, lines.Count(l => l.Kind == DiffLineKind.Removed));
        Assert.Equal(3000, lines.Count(l => l.Kind == DiffLineKind.Added));
    }
}
