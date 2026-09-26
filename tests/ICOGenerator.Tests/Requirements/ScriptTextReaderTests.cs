using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bộ tách chuỗi trong script của mockup không phải parser JS, nên các ca dễ đoán sai (biểu thức chính quy có
// dấu nháy, chữ JSX có dấu nháy, template lồng biểu thức) phải chỉ hỏng CỤC BỘ — không được nuốt phần còn lại.
public class ScriptTextReaderTests
{
    private static List<string> Read(string code) => ScriptTextReader.ReadLines(code, new HashSet<string>(StringComparer.Ordinal));

    [Fact]
    public void RegexLiteralWithQuote_DoesNotDerailTheRest()
    {
        var lines = Read("const re = /it's/g;\nconst label = 'Nhãn thật';");

        Assert.Equal(new[] { "Nhãn thật" }, lines);
    }

    [Fact]
    public void Division_IsNotMistakenForRegex()
    {
        var lines = Read("const pct = used / total; const title = 'Tỉ lệ lấp đầy';");

        Assert.Equal(new[] { "Tỉ lệ lấp đầy" }, lines);
    }

    [Fact]
    public void TemplateLiteral_KeepsStaticParts_AndStringsInsideExpressions()
    {
        var lines = Read("const m = `Tổng ${n > 1 ? 'nhiều tủ' : 'một tủ'} đã cấp`;");

        Assert.Equal(new[] { "Tổng … đã cấp · nhiều tủ · một tủ" }, lines);
    }

    [Fact]
    public void ApostropheInJsxText_OnlyCostsItsOwnLine()
    {
        var lines = Read("const A = () => <p>Don't do it</p>;\nconst B = 'Dòng sau vẫn đọc được';");

        Assert.Contains("Dòng sau vẫn đọc được", lines);
    }

    [Fact]
    public void UnicodeEscapes_AreDecoded()
    {
        var lines = Read("const s = 'Tr\\u1ea1ng th\\u00e1i'; const e = \"\\u{1F512} Kh\\xf3a\";");

        Assert.Equal(new[] { "Trạng thái · 🔒 Khóa" }, lines);
    }

    [Fact]
    public void Comments_AreSkipped()
    {
        var lines = Read("// it's a comment 'không phải chuỗi'\n/* \"cũng không\" */ const t = 'Nhãn';");

        Assert.Equal(new[] { "Nhãn" }, lines);
    }

    [Fact]
    public void RepeatedValues_AreDropped_ButEachSourceLineStaysAGroup()
    {
        var lines = Read("[{area:'Building 101', type:'shoe'},\n {area:'Building 101', type:'clothes'}]");

        Assert.Equal(new[] { "Building 101 · shoe", "clothes" }, lines);
    }

    [Fact]
    public void CssClassListsAndSelectors_AreNoise()
    {
        var lines = Read(
            "el.style = 'display:flex;gap:8px'; c = 'flex items-center gap-2'; q = 'section.page-view';"
            + " b = ';border:2px solid'; g = 'repeat(3,1fr)'; x = '#fff'; s = 'Trạng thái';");

        Assert.Equal(new[] { "Trạng thái" }, lines);
    }
}
