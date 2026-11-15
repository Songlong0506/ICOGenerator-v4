using System.Text.RegularExpressions;
using ICOGenerator.Localization;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Localization;

// Loc là chỗ DUY NHẤT backend lấy câu chữ viết cho người dùng, và nó cố ý KHÔNG ném khi thiếu khoá (một
// lượt chat không được chết vì một chuỗi) — thiếu khoá thì trả chính khoá. Cái giá của lựa chọn đó là một
// khoá gõ sai chỉ lộ ra trên màn hình của người dùng, dưới dạng "BAChatService.RunTurnAsync.3". Các test ở
// đây là chỗ bắt nó lúc build.
public class LocTests
{
    [Fact]
    public void EveryKeyUsedInTheSourceExistsInTheResourceFile()
    {
        var root = RepoRoot();
        var keys = ResourceKeys(root);
        var call = new Regex(@"Loc\.(?:T|In\([^,]+,)\s*\(?\s*""([^""]+)""");

        var missing = new List<string>();
        foreach (var folder in new[] { "Application", "Contracts", "Controllers", "Domain", "Extensions", "Services", "Localization" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match match in call.Matches(File.ReadAllText(file)))
                {
                    var key = match.Groups[1].Value;
                    // Khoá ghép lúc chạy ("CoverageGroup.Label." + key) kết thúc bằng dấu chấm: phần đuôi do
                    // một danh mục đóng quyết định và có test riêng của danh mục đó.
                    if (key.EndsWith('.'))
                    {
                        if (!keys.Any(k => k.StartsWith(key, StringComparison.Ordinal)))
                            missing.Add($"{Path.GetFileName(file)}: {key}*");
                    }
                    else if (!keys.Contains(key))
                    {
                        missing.Add($"{Path.GetFileName(file)}: {key}");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, "Khoá dùng trong code nhưng không có trong Localization/Text.resx:\n" + string.Join("\n", missing));
    }

    // Tham số attribute phải là hằng, nên [RequireProjectAccess(Message = "Project.NotFound")] ghi KHOÁ và
    // filter tra nó lúc trả lỗi. Khoá gõ sai ở đó sẽ hiện nguyên văn trong body của 404.
    [Fact]
    public void EveryResourceKeyOnAnAccessAttributeExists()
    {
        var root = RepoRoot();
        var keys = ResourceKeys(root);
        var message = new Regex(@"Message\s*=\s*""([A-Za-z]+(?:\.[A-Za-z]+)+)""");

        var missing = Directory.EnumerateFiles(Path.Combine(root, "Controllers"), "*.cs")
            .SelectMany(file => message.Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .Where(key => !keys.Contains(key))
            .Distinct()
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void AMissingKeyComesBackAsItself_InsteadOfThrowing()
        => Assert.Equal("Khong.Co.Khoa.Nay", Loc.T("Khong.Co.Khoa.Nay"));

    [Fact]
    public void TheLanguageIsScoped_AndFallsBackToTheDefault()
    {
        Assert.Equal(Loc.DefaultLanguage, Loc.Language);

        using (Loc.Use("xx-KhongCo"))
        {
            // Ngôn ngữ lạ ⇒ ngôn ngữ gốc, không phải một culture rỗng trả về khoá trần.
            Assert.Equal(Loc.DefaultLanguage, Loc.Language);
            Assert.Equal("luồng chính", Loc.T("FlowKind.Label.main"));
        }

        Assert.Equal(Loc.DefaultLanguage, Loc.Language);
    }

    // Tham số điền theo string.Format: một chuỗi nội suy chuyển sang resource phải cho ra đúng câu cũ.
    [Fact]
    public void ArgumentsAreFilledIntoThePlaceholders()
        => Assert.Equal("Mình (bản sao)", Loc.T("CloneProjectUseCase.BuildName", "Mình"));

    // Lượt người dùng GỬI BẢNG được nhận ra (UserTurnOrigin) bằng cụm mở đầu chung của các câu dẫn do
    // *Builder.RenderUserMessage soạn. Cụm và các câu đều nằm ở resource; một bản dịch đổi câu dẫn mà quên
    // cụm thì lượt gửi bảng bị coi là lời người dùng TỰ GÕ và lọt vào hồ sơ user như tính cách của họ.
    [Theory]
    [InlineData("FlowMapBuilder.RenderUserMessage")]
    [InlineData("ReportMapBuilder.RenderUserMessage")]
    [InlineData("ReportMapBuilder.RenderUserMessage.2")]
    [InlineData("ScreenScopeMapBuilder.RenderUserMessage")]
    [InlineData("PermissionMatrixBuilder.RenderUserMessage")]
    [InlineData("NotificationMapBuilder.RenderUserMessage")]
    [InlineData("NotificationMapBuilder.RenderUserMessage.2")]
    public void EveryTableSubmissionIntroStartsWithTheSharedPrefix_InEveryLanguage(string introKey)
    {
        foreach (var language in Loc.SupportedLanguages)
        {
            var prefix = Loc.In(language, UserTurnOrigin.TableSubmissionPrefixResource);
            Assert.StartsWith(prefix, Loc.In(language, introKey), StringComparison.Ordinal);
        }
    }

    private static HashSet<string> ResourceKeys(string root)
        => Regex.Matches(File.ReadAllText(Path.Combine(root, "Localization", "Text.resx")), @"<data name=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ICOGenerator.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Không tìm thấy thư mục gốc của repo từ " + AppContext.BaseDirectory);
    }
}
