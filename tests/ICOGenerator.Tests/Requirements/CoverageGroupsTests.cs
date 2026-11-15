using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Localization;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Mọi cổng và guard phía yêu cầu nhận ra một nhóm của bản đồ bao phủ bằng KHOÁ (CoverageGroupKey), và khoá
// đó có ba chỗ phải khớp nhau: hằng trong code, trường `key` ở khung của requirement-coverage.v5.md (thứ
// model chép lại), và nhãn ở resource (thứ dùng để suy khoá khi model bỏ sót `key` hoặc chỉ viết nhãn vào
// trường `group` của một câu hỏi).
//
// Lệch một trong ba thì hỏng theo kiểu tệ nhất: cổng đọc ra "nhóm chưa [RÕ]" và im lặng KHÔNG BAO GIỜ MỞ.
// Không lỗi, không log, chỉ là một cái bảng không bao giờ được bày ra.
public class CoverageGroupsTests
{
    [Fact]
    public void ThePromptTemplateCarriesExactlyTheKeysOfTheCatalog_InOrder()
    {
        var skeleton = CoverageChecklist.Parse(CoveragePromptFixture.Read());

        Assert.Equal(CoverageGroupKey.All, skeleton.Select(x => x.Key).ToList());
    }

    [Fact]
    public void TheResourceLabelOfEveryKeyIsTheLabelInThePrompt()
    {
        foreach (var group in CoverageChecklist.Parse(CoveragePromptFixture.Read()))
            Assert.Equal(group.Label, Loc.In(Loc.DefaultLanguage, "CoverageGroup.Label." + group.Key));
    }

    [Fact]
    public void ALabelIsResolvedToItsKey_EvenWhenTheModelDroppedTheKey()
    {
        var item = new CoverageMapItem { Label = "Phân quyền theo nghiệp vụ" };

        Assert.Equal(CoverageGroupKey.Permissions, item.Key);
    }

    [Fact]
    public void AnUnknownKeyFromTheModelFallsBackToTheLabel()
    {
        var item = new CoverageMapItem { Label = "Báo cáo / thống kê", Key = "bao-cao" };

        Assert.Equal(CoverageGroupKey.Reports, item.Key);
    }

    // Model hay rút gọn nhãn ("Luồng ngoại lệ" thay cho "Luồng ngoại lệ & trường hợp đặc biệt") — vẫn là
    // một nhóm. So nguyên văn ở đây là làm mọi luật của nhóm ngoại lệ câm trong im lặng.
    [Theory]
    [InlineData("Luồng ngoại lệ", CoverageGroupKey.Exceptions)]
    [InlineData("Luồng ngoại lệ & trường hợp đặc biệt", CoverageGroupKey.Exceptions)]
    [InlineData("báo cáo", CoverageGroupKey.Reports)]
    [InlineData("main-flow", CoverageGroupKey.MainFlow)]
    public void KeyOf_MatchesByPrefixBothWays(string group, string expected)
        => Assert.Equal(expected, CoverageGroups.KeyOf(group));

    // "Quy" là tiền tố của ba nhãn (quy trình, quy tắc, quy mô): không chỉ tới nhóm nào. Gán bừa vào nhóm
    // đầu tiên là để một cổng mở nhầm.
    [Theory]
    [InlineData("Quy")]
    [InlineData("Tích hợp hệ thống ngoài")]
    [InlineData("")]
    [InlineData(null)]
    public void KeyOf_ReturnsNull_WhenNoSingleGroupMatches(string? group)
        => Assert.Null(CoverageGroups.KeyOf(group));
}
