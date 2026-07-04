using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

public class PocSpecTests
{
    // The shape the BA prompt pins: one ### heading per screen in section 6, one bullet per rule in
    // section 10; other sections carry bullets/headings that must NOT leak into the checklist.
    private const string Spec = """
        # AI Design Spec
        ## 1. Project Goal
        - Quản lý mục tiêu cá nhân
        ## 5. Navigation Structure
        - Đăng nhập
        - Mục tiêu
        ## 6. Screens To Generate
        ### 6.1. Màn hình: Đăng nhập (/login)
        - Form: username, password
        ### 6.2 Danh sách mục tiêu
        - Bảng: tên, trọng số, trạng thái
        ### **Chấm điểm cuối năm**
        - Chỉ Manager thấy
        ## 7. UI/UX Direction
        ### Không phải màn hình
        - Sidebar trái
        ## 10. Business Rules
        - BR-1: Tổng trọng số của 5 mục tiêu phải bằng 100%
        - BR-2: Điểm trung bình = Σ(điểm × trọng số)
          - chi tiết lồng nhau không phải rule riêng
        1. BR-3: Sau khi ký thì khoá chỉnh sửa
        - N/A
        ## 11. Developer Instructions
        - Generate POC chạy được
        """;

    [Fact]
    public void ParsesScreenHeadings_CleaningNumberingLabelRouteAndEmphasis()
    {
        var spec = PocSpec.Parse(Spec);

        Assert.Equal(["Đăng nhập", "Danh sách mục tiêu", "Chấm điểm cuối năm"], spec.Screens);
    }

    [Fact]
    public void ParsesTopLevelRuleBullets_SkippingNestedDetailsAndPlaceholders()
    {
        var spec = PocSpec.Parse(Spec);

        Assert.Equal(3, spec.Rules.Count);
        Assert.Contains("BR-1: Tổng trọng số của 5 mục tiêu phải bằng 100%", spec.Rules);
        Assert.Contains("BR-3: Sau khi ký thì khoá chỉnh sửa", spec.Rules);
        Assert.DoesNotContain(spec.Rules, r => r.Contains("lồng nhau"));
        Assert.DoesNotContain(spec.Rules, r => r.Contains("N/A", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HeadingsOutsideTheScreensSection_AreNotScreens()
    {
        var spec = PocSpec.Parse(Spec);

        Assert.DoesNotContain(spec.Screens, s => s.Contains("Không phải màn hình"));
    }

    [Fact]
    public void SpecWithoutThePinnedShape_YieldsEmptyChecklist()
    {
        var spec = PocSpec.Parse("# AI Design Spec\n## 6. Screens To Generate\n- Đăng nhập: form login\n- Danh sách: bảng");

        Assert.Empty(spec.Screens); // bullets, not ### headings — old spec, coverage silently skipped
        Assert.Empty(spec.Rules);
        Assert.Same(PocSpec.Empty, spec);
    }

    [Fact]
    public void EmptyOrNullSpec_YieldsEmptyChecklist()
    {
        Assert.Same(PocSpec.Empty, PocSpec.Parse(null));
        Assert.Same(PocSpec.Empty, PocSpec.Parse("   "));
    }

    [Theory]
    [InlineData("Màn hình Đăng nhập", "Đăng nhập", true)]   // containment either way
    [InlineData("Đăng nhập", "Màn hình Đăng nhập", true)]
    [InlineData("Danh sách mục tiêu", "danh sách  mục tiêu", true)] // case/whitespace-insensitive
    [InlineData("Chấm điểm", "Đăng nhập", false)]
    [InlineData("Vô", "Vô cùng dài", false)] // too short for containment — exact only
    public void Matches_PairsSpecScreenWithPocLabel(string specScreen, string label, bool expected)
    {
        Assert.Equal(expected, PocSpec.Matches(specScreen, label));
    }
}
