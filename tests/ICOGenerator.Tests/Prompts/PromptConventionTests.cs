using Xunit;

namespace ICOGenerator.Tests.Prompts;

// Chốt chặn QUY ƯỚC HÌNH THỨC của thư mục /Prompts.
//
// Vì sao cần test chứ không chỉ ghi quy ước vào docs: prompt là file .md không được compiler soi, không ai
// "chạy" để thấy sai, và mỗi lần thêm một bước pipeline lại có thêm một file được chép từ file gần giống
// nhất rồi sửa. Sau vài vòng như vậy thì mỗi file một kiểu mở đầu, một tên mục đầu ra, một cách đánh dấu
// khối đầu vào — đúng tình trạng chắp vá mà đợt dọn này gỡ ra. Không có chốt chặn thì nó quay lại y hệt,
// và lần sau người đọc lại phải mở cả 37 file mới biết đâu là chuẩn.
//
// Test này CHỈ soi hình thức (heading, placeholder, hàng rào code). Nội dung/hành vi của từng prompt do
// các test riêng giữ (BAChatPlaybackRuleTests, BAChatScopeConflictRuleTests, CoverageChecklistTests…) và
// do golden set eval chấm.
public class PromptConventionTests
{
    // Bốn file KHÔNG mở đầu bằng "# Vai trò:" — có lý do, không phải sót:
    //  - organization-context/organization-scope: KHỐI NGỮ CẢNH được OrganizationContextService đính vào
    //    THÂN một prompt khác. Thêm H1 ở đây là chèn một tiêu đề cấp 1 vào giữa prompt của vai khác.
    //  - Shared/revision: khối NỐI SAU prompt gốc của bước, đã có H1 riêng của nó.
    //  - Shared/tool-agent-native: khung system prompt bọc quanh {{instruction}} của vai, bản thân nó
    //    không phải một vai.
    private static readonly HashSet<string> RoleHeadingExempt = new(StringComparer.OrdinalIgnoreCase)
    {
        "BusinessAnalyst/organization-context.v2.md",
        "BusinessAnalyst/organization-scope.v1.md",
        "Shared/revision.v1.md",
        "Shared/tool-agent-native.v1.md",
    };

    // Mọi placeholder xuất hiện trong /Prompts phải có chỗ thay ở C#. Danh sách này là bản đăng ký:
    // thêm một placeholder mới mà quên .Replace(...) thì prompt gửi đi mang nguyên chuỗi "{{...}}" tới
    // model — model không báo lỗi, nó chỉ trả lời tệ hơn, nên đây là loại lỗi không ai phát hiện ra.
    private static readonly HashSet<string> KnownPlaceholders = new(StringComparer.Ordinal)
    {
        "{{input}}",            // WorkflowTaskPromptBuilder, UatScenarioService, RequirementConflictService
        "{{persona}}",          // EvalRunnerService
        "{{instruction}}",      // AgentPromptBuilder
        "{{roleTitle}}",        // AgentPromptBuilder
        "{{previous_output}}",  // WorkflowTaskPromptBuilder (khối revision)
        "{{feedback}}",         // WorkflowTaskPromptBuilder (khối revision)
        "{{DEPARTMENTS}}",      // OrganizationContextService
        "{{POSITIONS}}",        // OrganizationContextService
        "{{TOTALS}}",           // OrganizationContextService
    };

    // Các tên mục đầu ra từng cùng tồn tại cho CÙNG một việc ("nói cho model biết phải trả về gì"):
    // "## ĐỊNH DẠNG ĐẦU RA", "## Yêu cầu đầu ra", "## Đầu ra", "## Định dạng". Một tên duy nhất để
    // người sửa prompt tìm đúng một chỗ, và để so hai prompt cạnh nhau không phải dịch tên mục.
    private const string OutputHeading = "## ĐỊNH DẠNG TRẢ LỜI (BẮT BUỘC)";

    [Theory]
    [MemberData(nameof(AllPrompts))]
    public void EveryPrompt_OpensWithItsRoleHeading(string key)
    {
        if (RoleHeadingExempt.Contains(key))
            return;

        var firstLine = Lines(key).FirstOrDefault(l => l.Trim().Length > 0) ?? string.Empty;

        Assert.True(
            firstLine.StartsWith("# Vai trò:", StringComparison.Ordinal),
            $"{key}: dòng đầu phải là '# Vai trò: <vai> — <việc>' để mở file ra là biết ngay ai làm gì. "
            + $"Đang là: '{firstLine}'. File thật sự không phải prompt của một vai thì thêm vào RoleHeadingExempt kèm lý do.");
    }

    [Theory]
    [MemberData(nameof(AllPrompts))]
    public void EveryPrompt_UsesTheOneOutputHeading(string key)
    {
        var text = Read(key);

        foreach (var stale in new[] { "## ĐỊNH DẠNG ĐẦU RA", "## Yêu cầu đầu ra", "## Đầu ra", "## Định dạng" })
        {
            Assert.False(
                text.Contains(stale, StringComparison.Ordinal),
                $"{key}: dùng '{stale}' cho mục đầu ra. Tên duy nhất được dùng là '{OutputHeading}'.");
        }
    }

    // Khối {{input}} là ranh giới giữa CHỈ DẪN và DỮ LIỆU. Đánh dấu nó bằng một heading thống nhất để
    // model không đọc nhầm dữ liệu thành chỉ dẫn, và để người đọc file thấy ngay prompt này nhận gì.
    [Theory]
    [MemberData(nameof(AllPrompts))]
    public void PromptsThatTakeInput_MarkTheInputBlock(string key)
    {
        var lines = Lines(key);

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("{{input}}", StringComparison.Ordinal)
                && !lines[i].Contains("{{persona}}", StringComparison.Ordinal))
            {
                continue;
            }

            var heading = lines.Take(i).LastOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal));
            Assert.True(
                heading != null && heading.StartsWith("# ĐẦU VÀO:", StringComparison.Ordinal),
                $"{key}: khối dữ liệu đầu vào phải nằm dưới một heading '# ĐẦU VÀO: <tên khối>'. "
                + $"Heading cấp 1 gần nhất đang là: '{heading ?? "(không có)"}'.");
            return;
        }
    }

    [Theory]
    [MemberData(nameof(AllPrompts))]
    public void EveryPrompt_OnlyUsesRegisteredPlaceholders(string key)
    {
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(Read(key), @"\{\{[^}\r\n]+\}\}"))
        {
            Assert.True(
                KnownPlaceholders.Contains(match.Value),
                $"{key}: placeholder {match.Value} không có trong bản đăng ký. Thêm chỗ .Replace(...) ở C# "
                + "rồi khai báo nó trong KnownPlaceholders — placeholder không được thay sẽ đi thẳng tới model.");
        }
    }

    // Ca thật đã gặp: checklist-gap/poc-feedback-gap viết "không markdown, không hàng rào ```" rồi ngay
    // bên dưới đặt ví dụ JSON TRONG một hàng rào ```. Model đọc được hai chỉ dẫn ngược nhau về đúng cái
    // định dạng nó phải trả về. (Parser thì khoan dung — LlmJson bóc hàng rào — nên lỗi này không bao giờ
    // nổ ra thành exception, nó chỉ âm thầm làm prompt khó tin.)
    [Theory]
    [MemberData(nameof(AllPrompts))]
    public void NoPrompt_BansCodeFencesWhileUsingThem(string key)
    {
        var text = Read(key);
        if (!text.Contains("```", StringComparison.Ordinal))
            return;

        Assert.False(
            text.Contains("không hàng rào", StringComparison.OrdinalIgnoreCase),
            $"{key}: cấm hàng rào ``` nhưng chính file lại đặt ví dụ trong một hàng rào — hai chỉ dẫn ngược nhau.");
    }

    public static TheoryData<string> AllPrompts()
    {
        var data = new TheoryData<string>();
        foreach (var full in Directory.EnumerateFiles(Root(), "*.md", SearchOption.AllDirectories).OrderBy(x => x))
            data.Add(Path.GetRelativePath(Root(), full).Replace(Path.DirectorySeparatorChar, '/'));
        return data;
    }

    private static string Read(string key) =>
        File.ReadAllText(Path.Combine(Root(), key.Replace('/', Path.DirectorySeparatorChar)));

    private static string[] Lines(string key) => Read(key).Replace("\r\n", "\n").Split('\n');

    // Cùng cách tìm Prompts/ như BAChatPlaybackRuleTests: ưu tiên bản copy trong bin, không có thì đi
    // ngược lên repo root.
    private static string Root()
    {
        var fromBin = Path.Combine(AppContext.BaseDirectory, "Prompts");
        if (Directory.Exists(fromBin))
            return fromBin;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Prompts");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("Không tìm thấy thư mục Prompts từ " + AppContext.BaseDirectory);
    }
}
