using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng tất định quyết định có chạy vòng bổ sung kịch bản hay không: MỌI câu nghiệm thu người dùng đã
// duyệt (§ 14) phải có ít nhất một kịch bản UAT chứng minh. Trang POC Review cũng đọc cùng định nghĩa
// "đã phủ" này, nên hai bên không bao giờ nói khác nhau về cùng một AC.
public class UatAcceptanceCoverageTests
{
    private const string Spec = """
        # AI Design Spec
        ## 14. Acceptance Criteria
        - AC-1 (Nhân viên gửi đơn): gửi xong thì đơn hiện ở danh sách chờ duyệt.
        - AC-2 (Quản lý duyệt đơn): duyệt xong thì đơn chuyển sang đã duyệt.
        """;

    private static UatScenarioSet SetWith(params string[][] acRefsPerScenario)
    {
        var set = new UatScenarioSet();
        for (var i = 0; i < acRefsPerScenario.Length; i++)
        {
            set.Scenarios.Add(new UatScenario
            {
                Title = $"Kịch bản {i + 1}",
                Steps = ["Bước 1"],
                AcRefs = acRefsPerScenario[i].ToList()
            });
        }
        return set;
    }

    [Fact]
    public void EveryCriterionCovered_YieldsNothingToFix()
    {
        var uncovered = UatScenarioService.FindUncoveredAcceptanceCriteria(
            PocSpec.Parse(Spec), SetWith(["AC-1"], ["AC-2"]));

        Assert.Empty(uncovered);
    }

    [Fact]
    public void CriterionWithoutAnyScenario_IsReported()
    {
        var uncovered = UatScenarioService.FindUncoveredAcceptanceCriteria(
            PocSpec.Parse(Spec), SetWith(["AC-1"]));

        Assert.Single(uncovered);
        Assert.Equal("AC-2", uncovered[0].Ref);
    }

    [Fact]
    public void OneScenarioMayCoverSeveralCriteria()
    {
        var uncovered = UatScenarioService.FindUncoveredAcceptanceCriteria(
            PocSpec.Parse(Spec), SetWith(["AC-1", "AC-2"]));

        Assert.Empty(uncovered);
    }

    // Model viết mã AC theo nhiều kiểu ("ac 2", "AC2"). So khớp phải bỏ qua khác biệt đó, nếu không cổng
    // báo thiếu cho một AC thật ra đã được phủ và đốt thêm một lời gọi LLM vô ích.
    [Theory]
    [InlineData("ac 2")]
    [InlineData("AC2")]
    [InlineData("ac-2")]
    public void RefFormatVariants_StillCount(string writtenRef)
    {
        var uncovered = UatScenarioService.FindUncoveredAcceptanceCriteria(
            PocSpec.Parse(Spec), SetWith(["AC-1"], [writtenRef]));

        Assert.Empty(uncovered);
    }

    [Fact]
    public void SpecWithoutAcceptanceSection_SkipsTheGateEntirely()
    {
        var spec = PocSpec.Parse("## 10. Business Rules\n- BR-1: x");

        Assert.Empty(UatScenarioService.FindUncoveredAcceptanceCriteria(spec, SetWith([])));
        Assert.Empty(UatScenarioService.FindUncoveredAcceptanceCriteria(spec, null));
    }
}
