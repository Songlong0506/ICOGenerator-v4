using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Đọc câu trả lời của lượt SINH / lượt SỬA của đường dựng POC một lần (PocOneShotBuilder). Chốt chặn quan
// trọng nhất là dòng ===POC_END===: thiếu nó thì câu trả lời đã bị cắt (chạm trần output) và bản demo cụt
// KHÔNG được ghi — caller rơi về đường agent nhiều bước.
public class PocOneShotReplyTests
{
    private const string Meta = """{"appName":"Leave","breadcrumb":"Home > Leave","navItems":[{"label":"Dashboard"},{"label":"Requests"}],"roles":["Employee","Manager"]}""";

    private static string BuildReply(string content, string script = "function pocSelfTest(){return [];}", bool end = true) =>
        "===POC_META===\n" + Meta + "\n" +
        "===POC_CONTENT===\n" + content + "\n" +
        "===POC_SCRIPT===\n" + script + "\n" +
        "===POC_HANDOFF===\n- Đã dựng 2 màn hình.\n" +
        (end ? "===POC_END===\n" : string.Empty);

    [Fact]
    public void Build_ReadsEveryBlock()
    {
        var draft = PocOneShotReply.TryParseBuild(BuildReply("<section class=\"page-view active\" data-view=\"Dashboard\">x</section>"), out var error);

        Assert.NotNull(draft);
        Assert.Equal(string.Empty, error);
        Assert.Equal("Leave", draft!.Meta.AppName);
        Assert.Equal("Home > Leave", draft.Meta.Breadcrumb);
        Assert.Equal(2, PocNavItem.ParseList(draft.Meta.NavItems!.Value).Count);
        Assert.Equal(new[] { "Employee", "Manager" }, PocRole.ParseList(draft.Meta.Roles!.Value));
        Assert.StartsWith("<section", draft.Content);
        Assert.Contains("pocSelfTest", draft.Script);
        Assert.Equal("- Đã dựng 2 màn hình.", draft.Handoff);
    }

    // Câu trả lời bị cắt giữa chừng: đọc được tới đâu cũng không dùng — script có thể đang dừng giữa hàm.
    [Fact]
    public void Build_RejectsAReplyWithoutTheEndMarker()
    {
        var draft = PocOneShotReply.TryParseBuild(BuildReply("<section>x</section>", end: false), out var error);

        Assert.Null(draft);
        Assert.Contains("POC_END", error);
    }

    [Theory]
    [InlineData("===POC_META===\n{not json}\n===POC_CONTENT===\n<section>x</section>\n===POC_END===")]
    [InlineData("===POC_CONTENT===\n<section>x</section>\n===POC_END===")]
    [InlineData("===POC_META===\n{}\n===POC_CONTENT===\n   \n===POC_END===")]
    [InlineData("Xin lỗi, tôi không làm được.")]
    public void Build_RejectsMissingOrUnreadableBlocks(string reply)
    {
        Assert.Null(PocOneShotReply.TryParseBuild(reply, out var error));
        Assert.NotEmpty(error);
    }

    // Model hay bọc khối code trong hàng rào dù không được yêu cầu; và chuỗi đánh dấu nằm GIỮA một dòng
    // (vd trong một literal JS) không phải dòng đánh dấu.
    [Fact]
    public void Build_StripsFences_AndIgnoresMarkersInsideALine()
    {
        var script = "```js\nvar s = 'x ===POC_END=== y';\n```";
        var draft = PocOneShotReply.TryParseBuild(BuildReply("```html\n<section>x</section>\n```", script), out _);

        Assert.NotNull(draft);
        Assert.Equal("<section>x</section>", draft!.Content);
        Assert.Equal("var s = 'x ===POC_END=== y';", draft.Script);
    }

    [Fact]
    public void Repair_ReadsEditPairsAndOptionalBlocks()
    {
        var reply =
            "===POC_FIND===\n  <td>A</td>\n===POC_REPLACE===\n  <td>B</td>\n" +
            "===POC_FIND===\nvar x = 1;\n===POC_REPLACE===\nvar x = 2;\n" +
            "===POC_APPEND_CONTENT===\n<section data-view=\"Requests\">r</section>\n" +
            "===POC_NOTES===\n- Thêm màn Requests.\n" +
            "===POC_END===";

        var patch = PocOneShotReply.TryParseRepair(reply, out var error);

        Assert.NotNull(patch);
        Assert.Equal(string.Empty, error);
        Assert.Equal(2, patch!.Edits.Count);
        Assert.Equal("  <td>A</td>", patch.Edits[0].Find); // giữ nguyên thụt đầu dòng — phải khớp từng ký tự
        Assert.Equal("var x = 2;", patch.Edits[1].Replace);
        Assert.Equal("<section data-view=\"Requests\">r</section>", patch.AppendContent);
        Assert.Null(patch.Script);
        Assert.Null(patch.Meta);
        Assert.False(patch.IsEmpty);
    }

    [Fact]
    public void Repair_RejectsAFindWithoutReplace_AndATruncatedReply()
    {
        Assert.Null(PocOneShotReply.TryParseRepair("===POC_FIND===\nx\n===POC_END===", out var orphan));
        Assert.Contains("POC_REPLACE", orphan);

        Assert.Null(PocOneShotReply.TryParseRepair("===POC_SCRIPT===\nfunction a(){", out var cut));
        Assert.Contains("POC_END", cut);
    }

    [Fact]
    public void Repair_WithNothingToApply_IsEmpty()
    {
        var patch = PocOneShotReply.TryParseRepair("===POC_NOTES===\n- Không còn gì để sửa.\n===POC_END===", out _);

        Assert.NotNull(patch);
        Assert.True(patch!.IsEmpty);
    }

    [Fact]
    public void ApplyEdits_AppliesUniqueMatches_InTheRegionWhereTheyMatch()
    {
        var regions = new[] { "<td>A</td><td>B</td>", "var total = 0;" };

        var (result, failures) = PocOneShotReply.ApplyEdits(regions,
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
    public void ApplyEdits_ReportsMissingAndAmbiguousMatches_WithoutTouchingTheText()
    {
        var regions = new[] { "<td>A</td><td>A</td>", "var x;" };

        var (result, failures) = PocOneShotReply.ApplyEdits(regions,
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
    public void ApplyEdits_FallsBackToTheTrimmedText()
    {
        var (result, failures) = PocOneShotReply.ApplyEdits(["<p>a</p>"], [new PocTextEdit("\n<p>a</p>\n", "\n<p>b</p>\n")]);

        Assert.Empty(failures);
        Assert.Equal("<p>b</p>", result[0]);
    }
}
