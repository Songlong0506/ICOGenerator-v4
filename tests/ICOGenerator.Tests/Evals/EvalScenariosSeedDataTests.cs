using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Evals;

// Chốt chặn cho golden set mặc định: mọi PromptKey trong seed phải trỏ tới file template CÓ THẬT dưới
// /Prompts (đổi tên/xoá template mà quên sửa seed sẽ fail ở đây thay vì âm thầm tạo scenario chết —
// EvalRunnerService sẽ báo lỗi từng scenario lúc chạy), và nội dung seed phải vừa các ràng buộc cột.
public class EvalScenariosSeedDataTests
{
    [Fact]
    public void Build_AllPromptKeysPointToExistingTemplates()
    {
        var promptsRoot = FindPromptsRoot();

        foreach (var scenario in EvalScenariosSeedData.Build())
        {
            var path = Path.Combine(promptsRoot, scenario.PromptKey.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Scenario '{scenario.Name}' trỏ tới template không tồn tại: {scenario.PromptKey}");
        }
    }

    [Fact]
    public void Build_ScenariosSatisfyColumnConstraintsAndAreActive()
    {
        var scenarios = EvalScenariosSeedData.Build();

        Assert.NotEmpty(scenarios);
        Assert.Equal(scenarios.Length, scenarios.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count());

        foreach (var scenario in scenarios)
        {
            Assert.False(string.IsNullOrWhiteSpace(scenario.Name));
            Assert.False(string.IsNullOrWhiteSpace(scenario.PromptKey));
            Assert.False(string.IsNullOrWhiteSpace(scenario.UserInput));
            Assert.False(string.IsNullOrWhiteSpace(scenario.Criteria));
            Assert.True(scenario.Name.Length <= 200, $"Name quá 200 ký tự: {scenario.Name}");
            Assert.True(scenario.PromptKey.Length <= 300, $"PromptKey quá 300 ký tự: {scenario.PromptKey}");
            Assert.True(scenario.IsActive, $"Scenario seed phải bật mặc định: {scenario.Name}");
        }
    }

    // Prompts/ được copy vào output của app (None Update + PreserveNewest) và flow sang bin của test qua
    // ProjectReference; nếu môi trường build không copy transitives thì đi ngược từ BaseDirectory lên
    // repo root (nơi có Prompts/BusinessAnalyst).
    private static string FindPromptsRoot()
    {
        var fromBin = Path.Combine(AppContext.BaseDirectory, "Prompts");
        if (Directory.Exists(Path.Combine(fromBin, "BusinessAnalyst")))
            return fromBin;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Prompts");
            if (Directory.Exists(Path.Combine(candidate, "BusinessAnalyst")))
                return candidate;
        }

        throw new DirectoryNotFoundException("Không tìm thấy thư mục Prompts từ " + AppContext.BaseDirectory);
    }
}
