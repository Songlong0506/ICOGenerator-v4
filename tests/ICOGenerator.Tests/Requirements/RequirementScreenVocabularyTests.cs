using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// MÀN REQUIREMENTS ĐƯỢC VẼ HAI LẦN, NÊN CHỮ CỦA NÓ PHẢI CÓ ĐÚNG MỘT CHỖ.
//
// Server vẽ trang lúc tải / sau F5; requirements.js vẽ lại chính các bảng và panel ấy ở frame `done` của
// mỗi lượt chat. Hai bản markup buộc phải khớp nhau, và cách rẻ nhất để chúng khớp — chép chữ sang JS —
// là cách chắc chắn nhất để chúng lệch: bản chép không có gì bắt nó đổi theo. Triệu chứng thì im lặng và
// muộn (đổi nhãn ở Razor xong, thêm một dòng bảng trong cùng phiên chat thì dòng mới mang nhãn cũ), nên
// nó phải được bắt ở đây chứ không phải ở mắt người review.
//
// Nay RequirementScreenText.Vocabulary() gửi cả khối xuống window.REQUIREMENTS_VOCAB và JS chỉ đọc.
// Hai phép thử dưới đây giữ đúng hai đầu của đường đó: JS không đọc khoá server không gửi, và các bản
// chép cũ không mọc lại.
public class RequirementScreenVocabularyTests
{
    private const string ScriptPath = "wwwroot/js/requirements.js";

    [Fact]
    public void Moi_khoa_JS_doc_deu_co_trong_khoi_tu_vung()
    {
        var script = Code(ScriptPath);
        var labels = Labels();
        var status = Status();

        var missingLabels = Keys(script, "REQ_TEXT").Where(k => !labels.ContainsKey(k)).ToList();
        var missingStatus = Keys(script, "COVERAGE_STATUS").Where(k => !status.ContainsKey(k)).ToList();

        // Khoá thiếu KHÔNG nổ ra lỗi lúc chạy: JS ghép `undefined` vào markup, hoặc so một trạng thái với
        // undefined nên không bao giờ khớp và thanh tiến độ đứng im ở 0%.
        Assert.True(missingLabels.Count == 0,
            "requirements.js đọc REQ_TEXT." + string.Join(", REQ_TEXT.", missingLabels)
            + " nhưng RequirementScreenText.Vocabulary() không gửi khoá đó xuống.");
        Assert.True(missingStatus.Count == 0,
            "requirements.js đọc COVERAGE_STATUS." + string.Join(", COVERAGE_STATUS.", missingStatus)
            + " nhưng khối từ vựng không có khoá đó.");
    }

    [Fact]
    public void JS_khong_khai_lai_chu_da_co_o_server()
    {
        var script = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), ScriptPath)));

        // Chỉ soi những giá trị KHÔNG thể trùng với văn xuôi: bốn trạng thái viết hoa, hai loại luồng và
        // các nhãn dài. Cố ý BỎ QUA "của mình" / "tất cả" (PermissionScope) — chúng là từ tiếng Việt bình
        // thường, có mặt trong chính các câu hướng dẫn mà JS dựng, nên soi chúng là tự báo động giả.
        var copied = CoverageStatus.All
            .Concat(new[] { FlowKind.Happy, FlowKind.Exception })
            .Concat(Labels().Values)
            .Where(value => script.Contains($"\"{value}\"", StringComparison.Ordinal)
                            || script.Contains($"`{value}`", StringComparison.Ordinal)
                            || script.Contains($"'{value}'", StringComparison.Ordinal))
            .ToList();

        Assert.True(copied.Count == 0,
            "requirements.js khai lại chữ server đã gửi: " + string.Join(" | ", copied)
            + ". Đọc từ window.REQUIREMENTS_VOCAB (REQ_TEXT / COVERAGE_STATUS / FLOW_KIND_*) thay vì chép.");
    }

    private static IReadOnlyDictionary<string, string> Labels()
        => (IReadOnlyDictionary<string, string>)RequirementScreenText.Vocabulary()["labels"];

    private static IReadOnlyDictionary<string, string> Status()
        => (IReadOnlyDictionary<string, string>)RequirementScreenText.Vocabulary()["status"];

    private static IEnumerable<string> Keys(string script, string holder)
        => Regex.Matches(script, Regex.Escape(holder) + @"\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

    // Chỉ soi CODE. Bỏ chú thích đi là bỏ luôn các đoạn giải thích chính luật này (chúng trích lại đúng
    // mấy chuỗi đang bị cấm chép). Phép cắt "//" thô có thể nuốt phần đuôi một dòng chứa "https://" —
    // hướng sai đó chỉ làm test bỏ sót, không bao giờ làm nó báo oan.
    private static string Code(string relativePath)
        => StripComments(File.ReadAllText(Path.Combine(RepoRoot(), relativePath)));

    private static string StripComments(string source)
    {
        var noBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlocks, @"//[^\n]*", string.Empty);
    }

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
