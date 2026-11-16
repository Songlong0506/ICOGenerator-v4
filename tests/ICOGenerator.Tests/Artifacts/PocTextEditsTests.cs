using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Các chỗ sửa "tìm → thay" của tool FixPocAndAudit trên vùng nội dung/vùng script của poc-demo.html.
public class PocTextEditsTests
{
    [Fact]
    public void Apply_AppliesUniqueMatches_InTheRegionWhereTheyMatch()
    {
        var regions = new[] { "<td>A</td><td>B</td>", "var total = 0;" };

        var (result, failures) = PocTextEdits.Apply(regions,
        [
            new PocTextEdit("<td>B</td>", "<td>C</td>"),
            new PocTextEdit("var total = 0;", "var total = sum();")
        ]);

        Assert.Empty(failures);
        Assert.Equal("<td>A</td><td>C</td>", result[0]);
        Assert.Equal("var total = sum();", result[1]);
    }

    // Luật của ReplaceInFile: khớp nhiều chỗ thì KHÔNG đoán chỗ nào — thay nhầm là hỏng một màn khác.
    [Fact]
    public void Apply_ReportsMissingAndAmbiguousMatches_WithoutTouchingTheText()
    {
        var regions = new[] { "<td>A</td><td>A</td>", "var x;" };

        var (result, failures) = PocTextEdits.Apply(regions,
        [
            new PocTextEdit("<td>A</td>", "<td>Z</td>"),
            new PocTextEdit("<td>Q</td>", "<td>Z</td>")
        ]);

        Assert.Equal(regions, result);
        Assert.Equal(2, failures.Count);
        Assert.Contains("khớp 2 chỗ", failures[0]);
        Assert.Contains("Không tìm thấy", failures[1]);
    }

    // Model hay lệch một dòng trống quanh đoạn cần tìm — thử lại bản đã bỏ khoảng trắng đầu/cuối.
    [Fact]
    public void Apply_FallsBackToTheTrimmedText()
    {
        var (result, failures) = PocTextEdits.Apply(["<p>a</p>"], [new PocTextEdit("\n<p>a</p>\n", "\n<p>b</p>\n")]);

        Assert.Empty(failures);
        Assert.Equal("<p>b</p>", result[0]);
    }
}
