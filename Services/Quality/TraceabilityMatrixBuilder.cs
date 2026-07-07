using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Quality;

/// <summary>Kết quả một lần dựng ma trận: hoặc ma trận + JSON chuẩn hoá + metadata, hoặc thông điệp lỗi.</summary>
public sealed record TraceabilityBuildOutcome(
    bool IsSuccess,
    string? Error,
    TraceabilityMatrix? Matrix,
    string? MatrixJson,
    string ModelName,
    int TotalTokens)
{
    public static TraceabilityBuildOutcome Fail(string error) => new(false, error, null, null, string.Empty, 0);
}

/// <summary>
/// Dựng ma trận truy vết cho MỘT project bằng MỘT lời gọi LLM: gom 4 nguồn — tài liệu yêu cầu (BRD, hoặc
/// Product Brief khi chưa có BRD; bản MỚI NHẤT trong DB), tài liệu User Stories, danh sách file code trong
/// workspace (04_Implementation/src) và báo cáo test (05_Test/test-report.md) — rồi để model đối chiếu theo
/// prompt Quality/traceability-matrix.v1.md và parse JSON trả về. Nguồn hạ nguồn nào chưa có thì ghi rõ
/// "(chưa có)" trong prompt — status tính trên các nguồn hiện có, dự án mới chạy nửa pipeline vẫn phân tích được.
/// <para>
/// Chạy bằng agent BA + model của nó (như các lời gọi phân tích requirement khác); lời gọi log vào
/// AgentModelCallLogs với Purpose "TraceabilityMatrix" nên chi phí tính vào project và qua budget guard.
/// Đây là thao tác ĐỒNG BỘ theo yêu cầu người dùng (bấm "Phân tích" → fetch chờ, như chat BA) — không
/// phải bước pipeline, không cần worker.
/// </para>
/// </summary>
public class TraceabilityMatrixBuilder
{
    // Nhiệt độ thấp: đối chiếu bằng chứng cần bám sát đầu vào, không cần sáng tạo.
    private const double Temperature = 0.1;
    // Trần mỗi khối đầu vào để tổng prompt không phình: 2 tài liệu + list file + báo cáo test ≈ 30-35KB.
    private const int MaxDocChars = 12000;
    private const int MaxTestReportChars = 8000;
    private const int MaxCodeFiles = 300;

    // Thư mục sinh ra bởi tooling — không phải code agent viết, đưa vào chỉ làm nhiễu danh sách file.
    private static readonly string[] SkippedDirNames = ["bin", "obj", "node_modules", ".git", "dist", ".vs"];

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly WorkspacePathResolver _workspacePaths;
    private readonly ILogger<TraceabilityMatrixBuilder> _logger;

    public TraceabilityMatrixBuilder(
        AppDbContext db,
        ILlmClient llm,
        PromptTemplateService prompts,
        WorkspacePathResolver workspacePaths,
        ILogger<TraceabilityMatrixBuilder> logger)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _workspacePaths = workspacePaths;
        _logger = logger;
    }

    public async Task<TraceabilityBuildOutcome> BuildAsync(Project project, CancellationToken cancellationToken = default)
    {
        var documents = await LoadLatestDocumentsAsync(project.Id, cancellationToken);
        var requirementDoc = documents.GetValueOrDefault("BRD.docx") ?? documents.GetValueOrDefault("ProductBrief.docx");
        if (requirementDoc == null)
            return TraceabilityBuildOutcome.Fail(
                "Dự án chưa có tài liệu yêu cầu (Product Brief/BRD) — hãy chạy \"Write Requirement\" trước.");

        var ba = await _db.Agents.AsNoTracking()
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst, cancellationToken);
        if (ba?.AiModel == null)
            return TraceabilityBuildOutcome.Fail("Chưa cấu hình agent BA / model cho agent BA.");

        var userInput = BuildUserInput(
            requirementDoc,
            documents.GetValueOrDefault("UserStories.docx"),
            ListImplementationFiles(project),
            ReadTestReport(project));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("Quality/traceability-matrix.v1.md")),
            new(ChatRole.User, userInput)
        };

        var result = await _llm.ChatWithLogAsync(
            ba.AiModel, messages, Temperature, new ModelCallLogContext(project.Id, ba, "TraceabilityMatrix"),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess)
            return TraceabilityBuildOutcome.Fail($"Lời gọi model lỗi: {result.ErrorMessage}");

        if (!TraceabilityMatrixParser.TryParse(result.Content, out var matrix) || matrix == null)
            return TraceabilityBuildOutcome.Fail(
                "Model trả về không đúng định dạng JSON của ma trận truy vết — hãy thử phân tích lại.");

        return new TraceabilityBuildOutcome(
            true, null, matrix, TraceabilityMatrixParser.Serialize(matrix), ba.AiModel.Name, result.TotalTokens);
    }

    // Tài liệu (Kind, Content) — bản MỚI NHẤT của mỗi loại trong DB (nội dung bị ghi đè tại chỗ ở nhiều
    // luồng nên bản mới nhất chính là trạng thái hiện hành; không lọc IsApproved — ma trận nên đọc trên
    // những gì đang có thật, kể cả bản draft).
    private async Task<Dictionary<string, string>> LoadLatestDocumentsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        string[] fileNames = ["BRD.docx", "ProductBrief.docx", "UserStories.docx"];

        var rows = await _db.ProjectDocuments.AsNoTracking()
            .Where(d => d.ProjectId == projectId && fileNames.Contains(d.FileName) && d.Content != "")
            .Select(d => new { d.FileName, d.Content, d.CreatedAt })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(d => d.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => d.CreatedAt).First().Content,
                StringComparer.OrdinalIgnoreCase);
    }

    // Danh sách đường dẫn tương đối (dấu "/") trong 04_Implementation/src — fail-open: workspace chưa
    // có/không đọc được thì trả rỗng, ma trận vẫn dựng được trên tài liệu.
    private IReadOnlyList<string> ListImplementationFiles(Project project)
    {
        try
        {
            var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
            var sourcePath = _workspacePaths.GetImplementationSourcePath(projectKey);
            if (!Directory.Exists(sourcePath))
                return [];

            return Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(sourcePath, f).Replace('\\', '/'))
                .Where(rel => !SkippedDirNames.Any(dir =>
                    rel.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)
                    || rel.Contains("/" + dir + "/", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
                .Take(MaxCodeFiles)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không liệt kê được file code của project {ProjectId} — ma trận dựng không có nguồn code.", project.Id);
            return [];
        }
    }

    private string? ReadTestReport(Project project)
    {
        try
        {
            var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
            var reportPath = Path.Combine(_workspacePaths.GetProjectWorkspacePath(projectKey), "05_Test", "test-report.md");
            if (!File.Exists(reportPath))
                return null;

            var content = File.ReadAllText(reportPath);
            return string.IsNullOrWhiteSpace(content) ? null : Truncate(content, MaxTestReportChars);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không đọc được báo cáo test của project {ProjectId} — ma trận dựng không có nguồn test.", project.Id);
            return null;
        }
    }

    private static string BuildUserInput(string requirementDoc, string? userStories, IReadOnlyList<string> codeFiles, string? testReport)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Tài liệu yêu cầu");
        sb.AppendLine(Truncate(requirementDoc.Trim(), MaxDocChars));
        sb.AppendLine();

        sb.AppendLine("## User Stories");
        sb.AppendLine(string.IsNullOrWhiteSpace(userStories) ? "(chưa có)" : Truncate(userStories.Trim(), MaxDocChars));
        sb.AppendLine();

        sb.AppendLine("## Danh sách file code (04_Implementation/src)");
        if (codeFiles.Count == 0)
        {
            sb.AppendLine("(chưa có)");
        }
        else
        {
            foreach (var file in codeFiles)
                sb.AppendLine($"- {file}");
        }
        sb.AppendLine();

        sb.AppendLine("## Báo cáo test");
        sb.AppendLine(string.IsNullOrWhiteSpace(testReport) ? "(chưa có)" : testReport.Trim());

        return sb.ToString();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n…(đã cắt bớt)";
}
