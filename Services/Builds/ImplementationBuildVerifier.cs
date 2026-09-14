using System.Text;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Tools;

namespace ICOGenerator.Services.Builds;

/// <summary>Kết quả một lượt chấm của cổng biên dịch.</summary>
/// <param name="Verdict">Kết luận máy-đọc-được, cũng là dòng nối vào cuối <c>AgentTask.Output</c>.</param>
/// <param name="Report">Báo cáo markdown đầy đủ (đã ghi ra <c>04_Implementation/build-report.md</c>).</param>
/// <param name="Summary">Một dòng cho mốc tiến độ trên UI.</param>
public record BuildVerificationResult(BuildVerdict Verdict, string Report, string Summary);

/// <summary>
/// CỔNG BIÊN DỊCH của bước Implementation: sau khi Developer báo xong, app TỰ chạy build trên chính
/// code vừa sinh và tự kết luận.
/// <para>
/// Vì sao phải là app chạy chứ không phải agent: prompt bước Implementation chỉ *khuyến khích*
/// (“nếu môi trường cho phép, dùng tool chạy lệnh build để xác nhận”), nên "code có biên dịch được
/// không" trước đây hoàn toàn phụ thuộc việc model có tự nguyện chạy build hay không — và nếu nó
/// không chạy thì không tầng nào phát hiện. Bước Testing cũng chỉ *có thể* build, còn
/// <see cref="Workflows.TestVerdictParser"/> coi verdict không rõ là PASS. Hệ quả: một dự án không
/// biên dịch nổi vẫn đi hết pipeline tới Pull Request mà không có chốt nào đỏ lên.
/// </para>
/// <para>
/// Đây là bản sao cho code thật của thứ bước POC đã có từ lâu (<c>AuditPocContent</c> +
/// <c>PlaywrightPocRuntimeChecker</c>): một tầng KIỂM BẰNG MÁY, độc lập với lời tự khai của agent.
/// </para>
/// <para>
/// FAIL-OPEN có chủ đích ở hai chỗ: không dò ra dự án nào để build, và bước cài phụ thuộc hỏng
/// (<c>npm install</c> trượt thường là mạng/registry, không phải code) ⇒ trả
/// <see cref="BuildVerdict.Skipped"/>. Chốt chặn này chỉ được phép chặn khi nó thật sự đo được cái gì.
/// </para>
/// </summary>
public class ImplementationBuildVerifier
{
    private readonly CommandTools _commandTools;
    private readonly WorkspaceTools _workspaceTools;
    private readonly WorkspacePathResolver _pathResolver;
    private readonly ILogger<ImplementationBuildVerifier> _logger;

    public ImplementationBuildVerifier(
        CommandTools commandTools,
        WorkspaceTools workspaceTools,
        WorkspacePathResolver pathResolver,
        ILogger<ImplementationBuildVerifier> logger)
    {
        _commandTools = commandTools;
        _workspaceTools = workspaceTools;
        _pathResolver = pathResolver;
        _logger = logger;
    }

    public async Task<BuildVerificationResult> VerifyAsync(
        string projectKey,
        IReadOnlyList<ProjectRepositorySlot> slots,
        Action<string, string, string?>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        // Cùng instance scoped mà agent run vừa dùng, nhưng set lại cho tường minh: cổng này cũng chạy
        // được ngoài một agent run (task BuildFix đầu vòng), và CommandTools giải mọi đường dẫn tương
        // đối theo CurrentWorkspacePath.
        _workspaceTools.SetWorkspace(projectKey);
        _workspaceTools.SetRunCancellation(cancellationToken);

        var workspaceRoot = _pathResolver.GetProjectWorkspacePath(projectKey);
        var commands = BuildCommandPlanner.Plan(workspaceRoot, slots);

        if (commands.Count == 0)
        {
            const string reason = "Không dò ra dự án nào build được trong `04_Implementation/src` (không có `.sln`/`.csproj`, và không có `package.json` khai script `build`).";
            return await FinishAsync(projectKey, BuildVerdict.Skipped, reason, [], cancellationToken);
        }

        var steps = new List<StepOutcome>();
        var verdict = BuildVerdict.Pass;
        string summary = $"Biên dịch sạch ({commands.Count} lệnh).";

        foreach (var command in commands)
        {
            onProgress?.Invoke("build", $"Cổng biên dịch: đang chạy `{string.Join(' ', command.Args)}` trong `{command.WorkingDirectory}`…", null);

            var result = await _commandTools.RunArgsAsync(command.Args, command.WorkingDirectory, _commandTools.BuildTimeoutSeconds);
            steps.Add(new StepOutcome(command, result.Succeeded, result.Output));

            if (result.Succeeded)
                continue;

            // Cài phụ thuộc hỏng = không kết luận được gì về CODE, nên dừng lượt chấm với SKIPPED thay
            // vì giao Developer đi sửa một lỗi nằm ngoài tầm với của nó.
            if (command.IsInstall)
            {
                verdict = BuildVerdict.Skipped;
                summary = $"Bỏ qua cổng biên dịch: `{command.Label}` không chạy được (thường là mạng/registry), không kết luận được về code.";
                _logger.LogWarning("Cổng biên dịch bỏ qua cho workspace {ProjectKey}: bước cài phụ thuộc {Label} thất bại.", projectKey, command.Label);
                break;
            }

            verdict = BuildVerdict.Fail;
            summary = $"Biên dịch THẤT BẠI ở `{command.Label}`.";
            break; // lỗi đầu tiên đã đủ để giao lại — chạy nốt các lệnh sau chỉ tốn thời gian.
        }

        return await FinishAsync(projectKey, verdict, summary, steps, cancellationToken);
    }

    private async Task<BuildVerificationResult> FinishAsync(
        string projectKey, BuildVerdict verdict, string summary, IReadOnlyList<StepOutcome> steps,
        CancellationToken cancellationToken)
    {
        var report = BuildReport(verdict, summary, steps);
        await TryWriteReportAsync(projectKey, report, cancellationToken);
        return new BuildVerificationResult(verdict, report, summary);
    }

    private static string BuildReport(BuildVerdict verdict, string summary, IReadOnlyList<StepOutcome> steps)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildVerdictParser.ReportHeading);
        sb.AppendLine();
        sb.AppendLine(BuildVerdictParser.Format(verdict));
        sb.AppendLine();
        sb.AppendLine(summary);

        foreach (var step in steps)
        {
            sb.AppendLine();
            sb.AppendLine($"### {step.Command.Label} — {(step.Succeeded ? "OK" : "THẤT BẠI")}");
            sb.AppendLine($"Lệnh: `{string.Join(' ', step.Command.Args)}` (thư mục `{step.Command.WorkingDirectory}`)");
            if (step.Succeeded)
                continue;

            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(BuildOutputDigest.Extract(step.Output));
            sb.AppendLine("```");
        }

        // Lặp lại dòng kết luận ở CUỐI, không chỉ ở đầu. Lý do là ở phía ĐỌC: khối này được nối vào
        // AgentTask.Output, và trang Delivery Quality thống kê bằng cách parse dòng đó — để nó nằm cuối
        // thì người đọc báo cáo dài cũng thấy kết luận mà không phải cuộn ngược, đúng khuôn
        // "VERDICT: PASS/FAIL là DÒNG CUỐI" mà Tester đã dùng. Parser lấy lần khớp cuối nên hai lần
        // xuất hiện cùng giá trị là vô hại.
        if (steps.Any(x => !x.Succeeded))
        {
            sb.AppendLine();
            sb.AppendLine(BuildVerdictParser.Format(verdict));
        }

        return sb.ToString().TrimEnd();
    }

    // Báo cáo là BẰNG CHỨNG ở cổng duyệt (người duyệt mở được đúng lỗi mà máy thấy) và là chỗ task sửa
    // lỗi đọc thêm khi phần digest trong prompt chưa đủ. Fail-open: ghi lỗi thì chỉ log — một cổng chấm
    // xong rồi không được phép làm gãy cả run chỉ vì không ghi nổi một file markdown.
    private async Task TryWriteReportAsync(string projectKey, string report, CancellationToken cancellationToken)
    {
        try
        {
            var folder = _pathResolver.GetPhasePath(projectKey, "04_Implementation");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, BuildVerdictParser.ReportFileName), report, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Không ghi được {FileName} cho workspace {ProjectKey}.", BuildVerdictParser.ReportFileName, projectKey);
        }
    }

    private sealed record StepOutcome(BuildCommand Command, bool Succeeded, string Output);
}
