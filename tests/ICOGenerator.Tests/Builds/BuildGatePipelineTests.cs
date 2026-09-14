using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Builds;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Workflows;
using ICOGenerator.Tests.Requirements;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Builds;

/// <summary>
/// Chu trình sửa lỗi biên dịch (Implementation ⇄ BuildFix) phải khớp đúng khuôn của chu trình
/// Testing ⇄ BugFix đã có: nằm NGOÀI chuỗi tuyến tính, tra được qua <c>Find</c>, và có trần vòng RIÊNG.
/// </summary>
public class BuildGatePipelineTests
{
    [Fact]
    public void Find_Resolves_The_BuildFix_Step()
    {
        var step = DeliveryPipeline.Find(WorkflowStageKey.BuildFix);

        Assert.NotNull(step);
        Assert.Equal(AgentRoleKey.Developer, step!.Role);
        Assert.Equal(AgentTaskType.BuildFix, step.TaskType);
    }

    // BuildFix là CHU TRÌNH quanh Implementation, không phải một bước của chuỗi: để nó lọt vào Steps là
    // dải timeline mọc thêm một bước mà người duyệt không bao giờ được hỏi tới.
    [Fact]
    public void BuildFix_Is_Outside_The_Linear_Chain()
    {
        Assert.DoesNotContain(DeliveryPipeline.Steps, s => s.Stage == WorkflowStageKey.BuildFix);
        Assert.Null(DeliveryPipeline.Next(WorkflowStageKey.BuildFix));
    }

    // Đây là lý do worker phải trả CurrentStage về Implementation khi vòng sửa build kết thúc: nếu
    // không, Next(BuildFix) = null và run bị đánh HOÀN TẤT ngay sau khi build xanh — mất sạch các bước
    // Review/Test/PR còn lại.
    [Fact]
    public void After_Implementation_Comes_CodeReview()
    {
        Assert.Equal(WorkflowStageKey.Implementation, DeliveryPipeline.ImplementationStep.Stage);
        Assert.Equal(WorkflowStageKey.CodeReview, DeliveryPipeline.Next(WorkflowStageKey.Implementation)?.Stage);
    }

    // Trần riêng, không dùng chung với vòng tự sửa lỗi kiểm thử: gộp chung thì một dự án tốn hai vòng
    // sửa build sẽ chỉ còn một vòng tự sửa cho cả chặng kiểm thử phía sau.
    [Fact]
    public void BuildFix_Has_Its_Own_Attempt_Ceiling()
    {
        Assert.True(DeliveryPipeline.MaxBuildFixAttempts > 0);
        Assert.NotSame(DeliveryPipeline.BuildFixStep, DeliveryPipeline.BugFixStep);
        Assert.NotEqual(DeliveryPipeline.BuildFixStep.TaskType, DeliveryPipeline.BugFixStep.TaskType);
    }

    [Fact]
    public void BuildFix_Task_Maps_To_Its_Own_Prompt_Template()
    {
        var prompts = new RecordingPrompts();

        var prompt = new WorkflowTaskPromptBuilder(prompts)
            .Build(AgentTaskType.BuildFix, "error CS0103", useBoschTemplate: false);

        Assert.Equal("Developer/build-fix.v1.md", prompts.LastPath);
        Assert.Contains("error CS0103", prompt);   // {{input}} đã được thay
    }

    // File prompt phải tồn tại THẬT: builder chỉ tra đường dẫn, còn thiếu file thì lỗi chỉ nổ ra lúc
    // chạy task đầu tiên — tức sau khi đã trả tiền cho cả bước Implementation.
    [Fact]
    public void BuildFix_Prompt_File_Exists_And_Carries_The_Input_Placeholder()
    {
        var text = PromptFixture.Read("Developer/build-fix.v1.md");

        Assert.Contains("{{input}}", text);
        Assert.Contains("SearchInFiles", text);   // tool điều hướng mà prompt bảo agent dùng
    }

    private sealed class RecordingPrompts : PromptTemplateService
    {
        public RecordingPrompts() : base(null!) { }

        public string? LastPath { get; private set; }

        public override string Get(string relativePath)
        {
            LastPath = relativePath;
            return "BASE:{{input}}";
        }
    }
}

/// <summary>
/// Cổng biên dịch trên một workspace KHÔNG có dự án nào build được: phải trả SKIPPED (không phải FAIL)
/// và vẫn ghi báo cáo. Đây là luật fail-open của chốt chặn — nó chỉ được phép chặn khi thật sự đo được
/// cái gì đó, chứ không được biến "không có gì để build" thành "code hỏng".
/// </summary>
public class ImplementationBuildVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ico-verify-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Skips_And_Reports_When_There_Is_Nothing_To_Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _root })
            .Build();

        var resolver = new WorkspacePathResolver(config);
        var workspaceTools = new WorkspaceTools(config, resolver, null!, null!);
        var verifier = new ImplementationBuildVerifier(
            new CommandTools(config, workspaceTools), workspaceTools, resolver,
            NullLogger<ImplementationBuildVerifier>.Instance);

        var slots = ProjectRepositoryLayout.Resolve(useBoschTemplate: false, null, null);
        var result = await verifier.VerifyAsync("proj", slots);

        Assert.Equal(BuildVerdict.Skipped, result.Verdict);
        Assert.Equal(BuildVerdict.Skipped, BuildVerdictParser.Parse(result.Report));

        var reportPath = Path.Combine(resolver.GetPhasePath("proj", "04_Implementation"), BuildVerdictParser.ReportFileName);
        Assert.True(File.Exists(reportPath));
        Assert.Contains("BUILD: SKIPPED", await File.ReadAllTextAsync(reportPath));
    }
}
