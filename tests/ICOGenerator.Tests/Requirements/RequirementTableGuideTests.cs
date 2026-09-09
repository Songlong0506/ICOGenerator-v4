using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// ĐOẠN HƯỚNG DẪN CỦA NĂM BẢNG CHỈ ĐƯỢC VIẾT MỘT LẦN.
//
// Các bảng phỏng vấn được vẽ hai lần (Razor lúc tải trang, requirements.js ở frame `done`), nên trước đây
// mỗi đoạn `.permmap-howto` / `.permmap-hint` tồn tại hai bản chép tay giống hệt — và bản chép thì không có
// gì bắt nó đổi theo. Khác nhãn nút, chữ ở đây dính thẻ <b> nên không nhét vào RequirementScreenText được;
// bản duy nhất nằm ở _TableGuide.cshtml, server in nó vào panel và vào <template id="guide-…">, JS đọc lại.
//
// Mối nối đó compiler không kiểm được nên ba phép thử dưới đây giữ cả ba đầu: khoá JS hỏi phải có thật,
// khoá khai ra phải có nhánh dựng, và không bên nào được khai lại hai lớp div ấy để bản chép mọc lại.
public class RequirementTableGuideTests
{
    private const string ScriptPath = "wwwroot/js/requirements.js";
    private const string ViewPath = "Views/Requirements/Index.cshtml";
    private const string PartialPath = "Views/Requirements/_TableGuide.cshtml";

    [Fact]
    public void Moi_khoa_JS_hoi_deu_co_trong_danh_sach_khoa()
    {
        var asked = Regex.Matches(Read(ScriptPath), @"tableGuide\(""(\w+)""\)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Khoá lạ KHÔNG nổ ra lỗi lúc chạy: getElementById trả null, bảng vẫn dựng nhưng mất hẳn phần
        // hướng dẫn — đúng kiểu hỏng thầm mà cả cơ chế này sinh ra để dẹp.
        var unknown = asked.Where(k => !RequirementTableGuide.Keys.Contains(k, StringComparer.Ordinal)).ToList();
        Assert.True(unknown.Count == 0,
            "requirements.js gọi tableGuide(" + string.Join("), tableGuide(", unknown)
            + ") nhưng RequirementTableGuide.Keys không có khoá đó.");

        // Chiều ngược lại: khai một khoá rồi không ai dùng thì thẻ <template> in ra vô ích.
        var unused = RequirementTableGuide.Keys.Where(k => !asked.Contains(k, StringComparer.Ordinal)).ToList();
        Assert.True(unused.Count == 0,
            "RequirementTableGuide.Keys khai " + string.Join(", ", unused) + " nhưng requirements.js không dùng.");
    }

    [Fact]
    public void Moi_khoa_deu_co_mot_nhanh_dung_trong_partial()
    {
        var partial = Read(PartialPath);
        var missing = RequirementTableGuide.Keys
            .Where(k => !partial.Contains($"case RequirementTableGuide.{PascalCase(k)}:", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "_TableGuide.cshtml thiếu nhánh cho: " + string.Join(", ", missing)
            + ". Khoá không có nhánh thì partial ném lúc render trang.");

        // Vòng lặp in <template> là đường DUY NHẤT đưa các khối này xuống cho JS; mất nó thì mọi bảng dựng
        // lại giữa phiên chat đều rụng phần hướng dẫn, mà lần tải trang đầu vẫn đúng nên rất dễ lọt.
        Assert.Contains("<template id=\"guide-@guideKey\">", Read(ViewPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Moi_khoa_duoc_dung_dung_mot_lan_o_panel()
    {
        var view = Read(ViewPath);

        // Vòng lặp in <template> dùng biến nên không lẫn vào phép đếm dưới đây; chỉ còn các lời gọi mà
        // từng panel tự viết ra. Một khoá xuất hiện hai lần = chép nhầm: panel này mang khối của panel kia,
        // và panel bị cướp khối thì im lặng mất phần hướng dẫn — bảng vẫn dựng nên không ai thấy.
        foreach (var key in RequirementTableGuide.Keys)
        {
            var used = Regex.Matches(view, @"model=""@RequirementTableGuide\." + PascalCase(key) + @"""(?!\w)").Count;
            Assert.True(used == 1,
                $"Index.cshtml gọi partial với khoá {PascalCase(key)} {used} lần — phải đúng 1 (mỗi panel một khối).");
        }
    }

    [Fact]
    public void Khong_ben_nao_khai_lai_khoi_huong_dan()
    {
        foreach (var path in new[] { ScriptPath, ViewPath })
        {
            var text = Read(path);
            foreach (var css in new[] { "permmap-howto", "permmap-hint" })
            {
                Assert.False(text.Contains($"class=\"{css}\"", StringComparison.Ordinal),
                    $"{path} khai lại <div class=\"{css}\"> — khối đó chỉ được viết ở {PartialPath}, "
                    + "hai bên còn lại gọi partial (Razor) hoặc tableGuide() (JS).");
            }
        }
    }

    // "flowMapHint" -> "FlowMapHint": tên hằng trong RequirementTableGuide viết hoa chữ đầu của khoá.
    private static string PascalCase(string key) => char.ToUpperInvariant(key[0]) + key[1..];

    private static string Read(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    // Đi ngược lên tới thư mục có ICOGenerator.csproj — cùng cách các test đọc file repo khác đang làm.
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ICOGenerator.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Không tìm thấy repo root từ " + AppContext.BaseDirectory);
    }
}
