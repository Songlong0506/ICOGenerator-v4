using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

/// <summary>
/// Kế hoạch thao tác (<see cref="UatScenario.StepActions"/>) là thứ duy nhất cho máy biết một câu bước gồm
/// những việc gì. Bất biến: thao tác của bước j luôn là của câu j — kế hoạch lệch bước nguy hiểm hơn không có
/// kế hoạch, vì máy sẽ làm đúng việc của bước khác rồi kết luận sai.
/// </summary>
public class UatScenarioPlanTests
{
    private const string DraftJson = """
        {
          "scenarios": [
            {
              "title": "Thêm danh mục",
              "screen": "Master Data",
              "ruleRefs": ["BR-16"],
              "steps": [
                { "text": "Chọn vai \"HR\" ở VIEW AS", "actions": [ { "kind": "role", "target": "HR" } ] },
                { "text": "   ", "actions": [ { "kind": "click", "target": "Rác" } ] },
                { "text": "Nhập Name là 'Recruitment Specialist', bấm Save",
                  "actions": [ { "kind": "FILL", "target": "Name", "value": "Recruitment Specialist" },
                               { "kind": "fill", "target": "Status" },
                               { "kind": "fly", "target": "Save" },
                               { "kind": "click", "target": "Save" } ] },
                { "text": "Kiểm tra dòng mới", "actions": [] }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Draft_IsSplitIntoStepsAndAlignedActions_AndJunkIsDropped()
    {
        var set = UatScenarioService.Sanitize(UatScenarioService.ParseFallback(DraftJson));

        var s = Assert.Single(set.Scenarios);
        Assert.True(s.HasActionPlan);
        Assert.Equal(3, s.Steps.Count);
        Assert.Equal(3, s.StepActions.Count);

        Assert.Equal(UatActionKind.Role, Assert.Single(s.ActionsOf(0)).Kind);
        // Bước rỗng bị bỏ CÙNG thao tác của nó; "fill" thiếu value và loại lạ bị bỏ; kind được chuẩn hoá.
        Assert.Equal(new[] { "fill:Name", "click:Save" }, s.ActionsOf(1).Select(a => $"{a.Kind}:{a.Target}").ToArray());
        Assert.Equal("Recruitment Specialist", s.ActionsOf(1)[0].Value);
        Assert.Empty(s.ActionsOf(2));
    }

    // Model phớt lờ định dạng mới (hoặc prompt bị Prompt Studio ghi đè bằng bản cũ): bước là chuỗi. Vẫn dùng
    // được — chỉ không có kế hoạch, lượt lái lùi về đoán theo câu chữ.
    [Fact]
    public void LegacyStringSteps_StillParse_WithoutAPlan()
    {
        var set = UatScenarioService.Sanitize(UatScenarioService.ParseFallback(
            """{ "scenarios": [ { "title": "Gửi đơn", "steps": ["Mở màn hình 'Đơn'", "Bấm \"Gửi\""] } ] }"""));

        var s = Assert.Single(set.Scenarios);
        Assert.Equal(2, s.Steps.Count);
        Assert.False(s.HasActionPlan);
        Assert.Empty(s.ActionsOf(0));
    }

    // File lưu từ trước (hoặc bị sửa tay) có số kế hoạch khác số bước ⇒ bỏ hẳn kế hoạch.
    [Fact]
    public void MisalignedPlan_IsDiscarded()
    {
        var set = UatScenarioService.Sanitize(new UatScenarioSet
        {
            Scenarios =
            {
                new UatScenario
                {
                    Title = "a",
                    Steps = ["một", "hai"],
                    StepActions = [[new UatAction { Kind = "click", Target = "X" }]]
                }
            }
        });

        Assert.False(set.Scenarios[0].HasActionPlan);
        Assert.Empty(set.Scenarios[0].StepActions);
    }

    [Fact]
    public void PlanRoundTripsThroughTheFile()
    {
        var set = UatScenarioService.Sanitize(UatScenarioService.ParseFallback(DraftJson));

        var back = UatScenarioService.Sanitize(System.Text.Json.JsonSerializer.Deserialize<UatScenarioSet>(UatScenarioService.ToJson(set))!);

        Assert.Equal(set.Scenarios[0].StepActions.Select(x => x.Count), back.Scenarios[0].StepActions.Select(x => x.Count));
        Assert.Equal("Recruitment Specialist", back.Scenarios[0].ActionsOf(1)[0].Value);
        Assert.DoesNotContain("HasActionPlan", UatScenarioService.ToJson(set));
    }

    // Agent dựng POC thấy TRƯỚC đúng chữ máy sẽ bấm/gõ — nhãn nút và nhãn ô phải trùng nguyên văn.
    [Fact]
    public void PromptBlock_TellsTheBuilderWhatTheMachineWillDo()
    {
        var set = UatScenarioService.Sanitize(UatScenarioService.ParseFallback(DraftJson));

        var block = UatScenarioService.BuildPromptBlock(set);

        Assert.Contains("máy làm: chọn vai «HR» ở VIEW AS", block);
        Assert.Contains("máy làm: gõ «Recruitment Specialist» vào ô «Name» → bấm «Save»", block);
        Assert.Contains("đừng thêm nút mang chữ đó chỉ để máy", block);
    }
}
