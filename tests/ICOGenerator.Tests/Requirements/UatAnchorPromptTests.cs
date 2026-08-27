using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

/// <summary>
/// Quy ước đánh số NEO CHỈ CHỖ phải giống hệt nhau ở ba tầng do ba nơi khác nhau sinh ra: khối prompt
/// giao đích cho agent, thuộc tính agent viết vào HTML (cổng <c>PocUatAnchors</c> đối chiếu), và
/// <c>data-anchor</c> mà trang POC Review in ra để tra lại phần tử. Lệch một nấc ở bất kỳ đâu là cả cơ
/// chế im lặng hỏng — không neo nào khớp mà chẳng tầng nào báo lỗi. Đây là chốt giữ ba tầng ấy khớp nhau.
/// </summary>
public class UatAnchorPromptTests
{
    private static UatScenarioSet Set() => new()
    {
        Scenarios = new List<UatScenario>
        {
            new() { Title = "Gửi đơn", Steps = new List<string> { "Mở màn hình 'Đơn'", "Bấm Gửi" } },
            new() { Title = "Duyệt đơn", Steps = new List<string> { "Chọn vai \"Manager\"", "Bấm Duyệt", "Kiểm tra trạng thái" } }
        }
    };

    [Fact]
    public void Token_IsOneBasedScenarioDotStep()
    {
        Assert.Equal("1.1", UatAnchor.Token(0, 0));
        Assert.Equal("2.3", UatAnchor.Token(1, 2));
        Assert.Equal("data-uat=\"2.3\"", UatAnchor.Markup(1, 2));
    }

    [Fact]
    public void PromptBlock_PrintsTheAnchorOfEveryStep()
    {
        var block = UatScenarioService.BuildPromptBlock(Set());

        Assert.Contains("data-uat=\"1.1\"", block);
        Assert.Contains("data-uat=\"1.2\"", block);
        Assert.Contains("data-uat=\"2.1\"", block);
        Assert.Contains("data-uat=\"2.3\"", block);
        // Mã neo đi KÈM câu bước, không phải một bảng riêng: agent đọc tới bước nào là thấy mã của bước đó.
        Assert.Contains("`data-uat=\"2.3\"` — Kiểm tra trạng thái", block);
    }

    // Khối prompt và cổng audit phải nói về CÙNG một tập mã: dán đúng các mã prompt in ra thì cổng sạch.
    [Fact]
    public void AnchorsCopiedFromThePromptSatisfyTheGate()
    {
        var set = Set();
        var html = string.Join("", set.Scenarios
            .SelectMany((s, i) => s.Steps.Select((_, j) => $"<button {UatAnchor.Markup(i, j)}>x</button>")));

        Assert.Empty(Services.Artifacts.PocUatAnchors.Check(set, html));
    }

    [Fact]
    public void PromptBlock_EmptySet_IsEmpty_SoTheOldPromptIsUnchanged()
    {
        Assert.Equal(string.Empty, UatScenarioService.BuildPromptBlock(null));
        Assert.Equal(string.Empty, UatScenarioService.BuildPromptBlock(new UatScenarioSet()));
    }
}
