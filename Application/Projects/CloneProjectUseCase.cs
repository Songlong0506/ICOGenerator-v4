using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum CloneProjectResult
{
    Cloned,
    ProjectNotFound,
    /// <summary>Tên bản sao rỗng sau khi trim VÀ dự án gốc cũng không có tên để dẫn xuất.</summary>
    NameRequired,
    /// <summary>Chép thư mục workspace thất bại ⇒ hủy toàn bộ, không tạo project trỏ vào thư mục trống.</summary>
    WorkspaceCopyFailed
}

/// <summary>
/// Nhân bản một dự án để thử nhiều tình huống khác nhau trên cùng một điểm xuất phát (cùng buổi phỏng vấn
/// BA, cùng file nguồn, cùng các bảng đã chốt) mà không phải phỏng vấn lại từ đầu.
///
/// Ba điều use case này cố ý KHÔNG làm, vì bản sao là dữ liệu thử chứ không phải một dự án thật thứ hai:
///
///  • <b>Không chép <c>AgentModelCallLogs</c></b> — đó là nguồn số liệu của trang Usage và Delivery
///    Quality; chép sang là nhân đôi chi phí đã tiêu trong báo cáo của cả tổ chức.
///  • <b>Không chép <c>PocShareLinks</c></b> — token là link công khai đang sống (unique index), nhân bản
///    nó là tự sinh thêm một cửa vào bản demo mà người tạo link không hề biết.
///  • <b>Không chép task đang dở</b> — <c>AgentTaskWorker</c> poll <c>Status == Queued</c> TOÀN CỤC (không
///    theo project), nên một task Queued chép sang sẽ được nhặt ngay và bắn lời gọi LLM thật. Task ở
///    Queued/Running/Retrying bị bỏ (chúng chưa có Output nào đáng giữ) và run của chúng thành Canceled.
///    Lưu ý <see cref="AgentTaskStatus"/> KHÔNG có giá trị Canceled — hủy là việc của
///    <see cref="WorkflowRunStatus"/>, xem ghi chú trong chính file enum đó.
///
/// Thứ tự thao tác theo đúng kỷ luật của <see cref="UpdateProjectUseCase"/>: chép đĩa TRƯỚC, lưu DB SAU
/// (Id project sinh ở client nên biết trước thư mục đích). Chép đĩa lỗi ⇒ không lưu gì; lưu DB lỗi ⇒ xóa
/// thư mục vừa chép rồi mới ném, để không bỏ lại rác chặn lần nhân bản sau.
/// </summary>
public class CloneProjectUseCase
{
    // Bản sao "chỉ phần yêu cầu" chỉ cần ĐẦU VÀO của buổi phỏng vấn; mọi thứ khác trong workspace là sản
    // phẩm sinh ra và sẽ được dựng lại từ đầu.
    private static readonly string[] RequirementOnlyFolders = { "00_Source" };

    private readonly AppDbContext _db;
    private readonly IArtifactStorage _artifactStorage;
    private readonly IAuditLogger _audit;

    public CloneProjectUseCase(AppDbContext db, IArtifactStorage artifactStorage, IAuditLogger audit)
    {
        _db = db;
        _artifactStorage = artifactStorage;
        _audit = audit;
    }

    public async Task<(CloneProjectResult Result, Guid? NewProjectId)> ExecuteAsync(
        CloneProjectVm vm, string? createdByUsername = null, CancellationToken cancellationToken = default)
    {
        var source = await _db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == vm.ProjectId, cancellationToken);
        if (source == null)
            return (CloneProjectResult.ProjectNotFound, null);

        var name = BuildName(vm.Name, source.Name);
        if (name.Length == 0)
            return (CloneProjectResult.NameRequired, null);

        var full = vm.Scope == ProjectCloneScope.Full;
        var clone = BuildProject(source, name, createdByUsername, full);

        var sourceKey = WorkspacePathResolver.GetWorkspaceFolder(source.Id, source.Name);
        var targetKey = WorkspacePathResolver.GetWorkspaceFolder(clone.Id, clone.Name);

        // Chép đĩa trước: một project trong DB trỏ vào thư mục trống là thứ không tự lành được, còn một
        // thư mục thừa trên đĩa thì xóa được (nhánh catch dưới).
        if (!_artifactStorage.TryCopyProjectWorkspace(sourceKey, targetKey, full ? null : RequirementOnlyFolders))
            return (CloneProjectResult.WorkspaceCopyFailed, null);

        try
        {
            // Bản sao "chỉ phần yêu cầu" vẫn cần bộ khung 5 giai đoạn như một project mới tạo.
            if (!full)
                _artifactStorage.InitializeProjectWorkspace(targetKey);
        }
        catch
        {
            // Best-effort y như CreateProjectUseCase — lần ghi đầu tiên sẽ tự tạo thư mục còn thiếu.
        }

        try
        {
            _db.Projects.Add(clone);

            var sourceFileIdMap = await CopySourceFilesAsync(source.Id, clone.Id, sourceKey, targetKey, cancellationToken);
            await CopyConversationsAsync(source.Id, clone.Id, sourceFileIdMap, cancellationToken);

            if (full)
            {
                await CopyDocumentsAsync(source.Id, clone.Id, sourceKey, targetKey, cancellationToken);
                await CopyWorkflowsAsync(source.Id, clone.Id, cancellationToken);
                clone.PocFeedbackHarvestedCount = await CopyPocCommentsAsync(source.Id, clone.Id, cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            _artifactStorage.TryDeleteProjectWorkspace(targetKey);
            throw;
        }

        var scopeLabel = full ? "toàn bộ" : "chỉ phần yêu cầu";
        await _audit.LogAsync(AuditCategory.Project, AuditAction.Create, clone.Id.ToString(),
            $"Nhân bản dự án \"{source.Name}\" → \"{clone.Name}\" ({scopeLabel})",
            after: new { clone.Id, clone.Name, SourceProjectId = source.Id, Scope = vm.Scope.ToString() },
            cancellationToken: cancellationToken);

        return (CloneProjectResult.Cloned, clone.Id);
    }

    private static string BuildName(string? requested, string sourceName)
    {
        var name = (requested ?? string.Empty).Trim();
        if (name.Length == 0)
            name = $"{sourceName.Trim()} (bản sao)";

        // Project.Name giới hạn 200 ký tự (AppDbContext) — hậu tố "(bản sao)" trên một tên đã dài sẽ vượt
        // trần và làm SaveChanges ném ở SqlServer.
        return name.Length > 200 ? name[..200].TrimEnd() : name;
    }

    private static Project BuildProject(Project source, string name, string? createdByUsername, bool full) => new()
    {
        // Id + CreatedAt để mặc định (mới). Chủ sở hữu là người bấm nhân bản, để bản sao hiện trong danh
        // sách của họ ngay cả khi họ không phải người tạo dự án gốc.
        Name = name,
        CreatedByUsername = createdByUsername,

        Description = source.Description,
        OrgUnitCode = source.OrgUnitCode,
        IsUseBoschTemplate = source.IsUseBoschTemplate,
        BackendGitUrl = source.BackendGitUrl,
        FrontendGitUrl = source.FrontendGitUrl,

        // Trí nhớ + trạng thái buổi phỏng vấn: chép nguyên ở CẢ HAI chế độ — đây chính là thứ đắt tiền mà
        // người dùng nhân bản để khỏi làm lại. Các CON TRỎ harvest phải giữ nguyên giá trị, không reset về
        // 0: reset đi thì bản sao sẽ chắt lọc LẠI đúng những lượt cũ vào AppUser.UserMemory và vào bản đồ
        // bao phủ, tức trả tiền model lần hai cho một kết quả đã có.
        ConversationSummary = source.ConversationSummary,
        SummarizedTurnCount = source.SummarizedTurnCount,
        UserMemoryHarvestedTurnCount = source.UserMemoryHarvestedTurnCount,
        DomainKey = source.DomainKey,
        RequirementCoverageMap = source.RequirementCoverageMap,
        CoverageHarvestedTurnCount = source.CoverageHarvestedTurnCount,
        DecisionLog = source.DecisionLog,
        DecisionHarvestedTurnCount = source.DecisionHarvestedTurnCount,
        OpenQuestions = source.OpenQuestions,
        PlannedScope = source.PlannedScope,
        WorkedExamples = source.WorkedExamples,
        InterviewOutlookHarvestedTurnCount = source.InterviewOutlookHarvestedTurnCount,
        PendingConflicts = source.PendingConflicts,
        ConflictCheckedTurnCount = source.ConflictCheckedTurnCount,
        SpecAssumptionCorrections = source.SpecAssumptionCorrections,
        ConfirmedAssumptions = source.ConfirmedAssumptions,

        // Hàng đợi học từ giả định bị bác KHÔNG chép: bài học thuộc về dự án gốc và sẽ được nó chắt lọc:
        // chép sang là hai dự án cùng đề xuất một bài học từ đúng một lần người dùng bấm "Chưa đúng".
        // Cùng lý do với ChecklistGapHarvested = true bên dưới.
        PendingAssumptionGaps = null,

        // Sáu bảng đã chốt của buổi phỏng vấn + danh sách người nhận đi kèm bảng thông báo.
        PermissionMatrix = source.PermissionMatrix,
        FlowMap = source.FlowMap,
        ScreenScopeMap = source.ScreenScopeMap,
        EntityMap = source.EntityMap,
        ReportMap = source.ReportMap,
        NotificationMap = source.NotificationMap,
        NotificationRecipients = source.NotificationRecipients,

        // Cổng xác nhận giả định trỏ tới một bản spec V{n} cụ thể — bản sao "chỉ phần yêu cầu" không có
        // tài liệu nào nên cổng đó sẽ chờ mãi một thứ không tồn tại.
        PendingAssumptionsVersion = full ? source.PendingAssumptionsVersion : null,

        // Nghiệm thu POC là chữ ký của một người thật cho một bản demo cụ thể — không nhân bản chữ ký.
        PocAcceptedAtUtc = null,
        PocAcceptedBy = null,

        // Rà soát "khoảng trống checklist" chỉ chạy MỘT LẦN mỗi dự án và ghi vào AgentChecklistItem dùng
        // chung cho MỌI dự án sau này. Đánh dấu bản sao là đã rà rồi để cùng một buổi phỏng vấn không đẻ ra
        // hai lần cùng một bài học. Cùng lý do với PocFeedbackHarvestedCount đặt theo số ghi chú chép sang.
        ChecklistGapHarvested = true,
        PocFeedbackHarvestedCount = 0
    };

    private async Task<Dictionary<Guid, Guid>> CopySourceFilesAsync(
        Guid sourceProjectId, Guid cloneProjectId, string sourceKey, string targetKey, CancellationToken cancellationToken)
    {
        var files = await _db.ProjectSourceFiles.AsNoTracking()
            .Where(f => f.ProjectId == sourceProjectId)
            .ToListAsync(cancellationToken);

        var idMap = new Dictionary<Guid, Guid>(files.Count);

        foreach (var file in files)
        {
            var copy = new ProjectSourceFile
            {
                ProjectId = cloneProjectId,
                Kind = file.Kind,
                FileName = file.FileName,
                ContentType = file.ContentType,
                SizeBytes = file.SizeBytes,
                // StoredPath là đường dẫn TUYỆT ĐỐI (ProjectSourceIngestor); không viết lại thì bản sao đọc
                // file của dự án gốc, và xóa nguồn ở bản sao sẽ xóa file thật của dự án gốc.
                // Tên thư mục con giữ nguyên id CŨ: không chỗ nào suy ngược thư mục từ Id, mọi nơi đều lấy
                // Path.GetDirectoryName(StoredPath) — xem DeleteProjectSourceUseCase / SourceContextBuilder.
                StoredPath = RewriteWorkspacePath(file.StoredPath, sourceKey, targetKey) ?? string.Empty,
                ExtractedText = file.ExtractedText,
                PageCount = file.PageCount,
                ColumnMap = file.ColumnMap,
                ScannedPageImageCount = file.ScannedPageImageCount,
                VisionSummary = file.VisionSummary,
                UploadedByUserId = file.UploadedByUserId,
                CreatedAt = file.CreatedAt
            };

            idMap[file.Id] = copy.Id;
            _db.ProjectSourceFiles.Add(copy);
        }

        return idMap;
    }

    private async Task CopyConversationsAsync(
        Guid sourceProjectId, Guid cloneProjectId, IReadOnlyDictionary<Guid, Guid> sourceFileIdMap, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters: AgentConversation có global filter ArchivedAt == null. Bỏ các lượt đã archive
        // sẽ làm lệch mọi con trỏ đếm-theo-CreatedAt đã chép sang ở trên (SummarizedTurnCount và họ hàng).
        var turns = await _db.AgentConversations.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.ProjectId == sourceProjectId)
            .ToListAsync(cancellationToken);

        foreach (var turn in turns)
        {
            _db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = cloneProjectId,
                AgentId = turn.AgentId,
                Role = turn.Role,
                Message = turn.Message,
                Suggestions = turn.Suggestions,
                SuggestionsMultiSelect = turn.SuggestionsMultiSelect,
                Questions = turn.Questions,
                ColumnMap = turn.ColumnMap,
                PermissionMatrix = turn.PermissionMatrix,
                FlowMap = turn.FlowMap,
                ScreenScopeMap = turn.ScreenScopeMap,
                EntityMap = turn.EntityMap,
                ReportMap = turn.ReportMap,
                NotificationMap = turn.NotificationMap,
                FlowDiagram = turn.FlowDiagram,
                Attachments = RemapAttachments(turn.Attachments, sourceFileIdMap),
                ReadinessVerified = turn.ReadinessVerified,
                TokenUsed = turn.TokenUsed,
                ArchivedAt = turn.ArchivedAt,
                CreatedAt = turn.CreatedAt
            });
        }
    }

    private async Task CopyDocumentsAsync(
        Guid sourceProjectId, Guid cloneProjectId, string sourceKey, string targetKey, CancellationToken cancellationToken)
    {
        var documents = await _db.ProjectDocuments.AsNoTracking()
            .Where(d => d.ProjectId == sourceProjectId)
            .ToListAsync(cancellationToken);

        var documentIds = documents.Select(d => d.Id).ToList();
        var revisions = await _db.ProjectDocumentRevisions.AsNoTracking()
            .Where(r => documentIds.Contains(r.ProjectDocumentId))
            .ToListAsync(cancellationToken);

        var revisionsByDocument = revisions.ToLookup(r => r.ProjectDocumentId);

        foreach (var document in documents)
        {
            var copy = new ProjectDocument
            {
                ProjectId = cloneProjectId,
                AgentId = document.AgentId,
                Folder = document.Folder,
                VersionName = document.VersionName,
                IsApproved = document.IsApproved,
                FileName = document.FileName,
                Content = document.Content,
                // FilePath khi thì tuyệt đối (RequirementDocumentGenerator) khi thì tương đối
                // (ApproveRequirementUseCase) — bản tương đối không chứa key nên đi qua nguyên vẹn.
                FilePath = RewriteWorkspacePath(document.FilePath, sourceKey, targetKey),
                TokenUsed = document.TokenUsed,
                CreatedAt = document.CreatedAt
            };
            _db.ProjectDocuments.Add(copy);

            foreach (var revision in revisionsByDocument[document.Id])
            {
                _db.ProjectDocumentRevisions.Add(new ProjectDocumentRevision
                {
                    ProjectDocumentId = copy.Id,
                    RevisionNumber = revision.RevisionNumber,
                    Content = revision.Content,
                    ChangeNote = revision.ChangeNote,
                    VersionName = revision.VersionName,
                    CreatedAt = revision.CreatedAt
                });
            }
        }
    }

    private async Task CopyWorkflowsAsync(Guid sourceProjectId, Guid cloneProjectId, CancellationToken cancellationToken)
    {
        var runs = await _db.WorkflowRuns.AsNoTracking()
            .Where(r => r.ProjectId == sourceProjectId)
            .ToListAsync(cancellationToken);

        var runIds = runs.Select(r => r.Id).ToList();
        var tasks = await _db.AgentTasks.AsNoTracking()
            .Where(t => runIds.Contains(t.WorkflowRunId))
            .ToListAsync(cancellationToken);

        var tasksByRun = tasks.ToLookup(t => t.WorkflowRunId);

        foreach (var run in runs)
        {
            var copy = new WorkflowRun
            {
                ProjectId = cloneProjectId,
                Name = run.Name,
                // Run đang chạy dở không thể tiếp tục ở bản sao (task của nó không được chép), nên chép
                // nguyên trạng Running sẽ để lại một run đứng hình mãi mãi. WaitingForHuman thì GIỮ NGUYÊN:
                // đó chính là cổng duyệt mà người ta nhân bản dự án để thử.
                Status = run.Status is WorkflowRunStatus.Queued or WorkflowRunStatus.Running
                    ? WorkflowRunStatus.Canceled
                    : run.Status,
                CurrentStage = run.CurrentStage,
                CreatedAt = run.CreatedAt,
                StartedAt = run.StartedAt,
                FinishedAt = run.FinishedAt
            };
            _db.WorkflowRuns.Add(copy);

            foreach (var task in tasksByRun[run.Id])
            {
                // Task dở dang chưa có Output nào đáng giữ, và một task Queued chép sang sẽ bị
                // AgentTaskWorker (poll toàn cục) nhặt ngay để bắn lời gọi LLM thật.
                if (task.Status is not (AgentTaskStatus.Completed or AgentTaskStatus.Failed))
                    continue;

                _db.AgentTasks.Add(new AgentTask
                {
                    WorkflowRunId = copy.Id,
                    ProjectId = cloneProjectId,
                    AgentId = task.AgentId,
                    Type = task.Type,
                    Status = task.Status,
                    Title = task.Title,
                    Input = task.Input,
                    RevisionFeedback = task.RevisionFeedback,
                    Output = task.Output,
                    Error = task.Error,
                    Attempt = task.Attempt,
                    CreatedAt = task.CreatedAt,
                    StartedAt = task.StartedAt,
                    FinishedAt = task.FinishedAt
                });
            }
        }
    }

    private async Task<int> CopyPocCommentsAsync(Guid sourceProjectId, Guid cloneProjectId, CancellationToken cancellationToken)
    {
        // PocComment không có navigation property trên Project — phải query thẳng bảng.
        var comments = await _db.PocComments.AsNoTracking()
            .Where(c => c.ProjectId == sourceProjectId)
            .ToListAsync(cancellationToken);

        foreach (var comment in comments)
        {
            _db.PocComments.Add(new PocComment
            {
                ProjectId = cloneProjectId,
                Target = comment.Target,
                BriefVersion = comment.BriefVersion,
                PageView = comment.PageView,
                ElementLabel = comment.ElementLabel,
                ElementPath = comment.ElementPath,
                XPercent = comment.XPercent,
                YPercent = comment.YPercent,
                Quote = comment.Quote,
                Comment = comment.Comment,
                Status = comment.Status,
                Route = comment.Route,
                CreatedByUsername = comment.CreatedByUsername,
                CreatedAt = comment.CreatedAt,
                AddressedAtUtc = comment.AddressedAtUtc,
                AddressedNote = comment.AddressedNote,
                WithdrawnAtUtc = comment.WithdrawnAtUtc,
                WithdrawnByUsername = comment.WithdrawnByUsername
                // RevisionTaskId KHÔNG chép: nó trỏ tới AgentTask của dự án GỐC, mà bản sao có bộ task id
                // riêng. Dòng lịch sử của bản sao vẫn còn bàn giao cắt gọn ở AddressedNote.
            });
        }

        // Con trỏ harvest đếm theo các ghi chú Sent (xem PocFeedbackMemoryService) — đếm cả ghi chú Brief
        // hay ghi chú đã thu hồi vào đây là đẩy con trỏ vượt quá, và bài học của những vòng SAU của bản sao
        // sẽ bị bỏ qua.
        return comments.Count(c => c.Status == PocCommentStatus.Sent);
    }

    /// <summary>
    /// Đổi phần key thư mục workspace trong một đường dẫn đã lưu. Làm bằng phép thay chuỗi thay vì
    /// <see cref="WorkspacePathResolver"/> để không phụ thuộc <c>AgentWorkspace:RootPath</c> (đường dẫn cũ
    /// có thể được ghi từ một máy khác) — key đã mang 8 ký tự đầu Id project nên không thể trùng nhầm.
    /// Đường dẫn tương đối không chứa key sẽ đi qua nguyên vẹn.
    /// </summary>
    private static string? RewriteWorkspacePath(string? path, string sourceKey, string targetKey) =>
        string.IsNullOrEmpty(path) ? path : path.Replace(sourceKey, targetKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>AgentConversation.Attachments</c> là JSON <c>ChatAttachment[]</c> mang Id của
    /// <c>ProjectSourceFile</c>. Bản sao có file nguồn Id MỚI, nên không remap thì bubble hội thoại của bản
    /// sao trỏ về file của dự án GỐC. Thay chuỗi theo dạng "D" — đúng dạng JsonSerializer ghi ra Guid.
    /// </summary>
    private static string? RemapAttachments(string? json, IReadOnlyDictionary<Guid, Guid> sourceFileIdMap)
    {
        if (string.IsNullOrEmpty(json) || sourceFileIdMap.Count == 0)
            return json;

        foreach (var (oldId, newId) in sourceFileIdMap)
            json = json.Replace(oldId.ToString(), newId.ToString(), StringComparison.OrdinalIgnoreCase);

        return json;
    }
}
