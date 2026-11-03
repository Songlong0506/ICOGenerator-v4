using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// File uat-scenarios.json là thứ người đọc mở ra xem, nên tiếng Việt phải giữ nguyên chữ (không \uXXXX)
// và vẫn đọc ngược lại được đúng như lúc ghi.
public class UatScenarioJsonTests
{
    [Fact]
    public void ToJson_KeepsVietnameseReadable_AndRoundTrips()
    {
        var set = new UatScenarioSet();
        set.Scenarios.Add(new UatScenario
        {
            Title = "Quản lý duyệt đơn",
            Screen = "Danh sách chờ duyệt",
            Steps = ["Bấm \"Duyệt\" ở đơn đầu tiên"]
        });

        var json = UatScenarioService.ToJson(set);

        Assert.DoesNotContain("\\u", json);
        Assert.Contains("Quản lý duyệt đơn", json);
        Assert.Contains("\n", json); // thụt dòng, không phải một dòng dài
        var back = System.Text.Json.JsonSerializer.Deserialize<UatScenarioSet>(json)!;
        Assert.Equal("Bấm \"Duyệt\" ở đơn đầu tiên", back.Scenarios.Single().Steps.Single());
    }
}
