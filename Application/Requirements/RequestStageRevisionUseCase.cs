using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum RequestStageRevisionResult
{
    /// <summary>Đã enqueue task chỉnh sửa cho bước hiện tại — worker sẽ chạy lại với nhận xét.</summary>
    Queued,

    /// <summary>Không có workflow nào đang chờ duyệt để yêu cầu chỉnh sửa.</summary>
    NoWaitingRun,

    /// <summary>Nhận xét trống — không có gì để agent sửa theo.</summary>
    MissingFeedback,

    /// <summary>Đã dùng hết số vòng chỉnh sửa cho bước này (xem <see cref="DeliveryPipeline.MaxRevisionRounds"/>).</summary>
    RevisionLimitReached,

    /// <summary>
    /// Có run đang chờ duyệt nhưng KHÔNG ở bước mà caller được phép chỉnh (xem tham số <c>onlyStage</c>) —
    /// dùng cho đường vào của user thường ở trang POC Review: họ chỉ được chỉnh bản demo, không được với
    /// tới các bước kỹ thuật phía sau.
    /// </summary>
    StageMismatch
}

/// <summary>
/// Cổng duyệt — lựa chọn thứ ba bên cạnh Duyệt/Từ chối: người duyệt thấy kết quả bước GẦN đúng,
/// gửi nhận xét để agent CHỈNH SỬA lại đúng bước đó thay vì hủy cả workflow (Reject) rồi chạy lại
/// từ đầu, vốn đốt lại token của mọi bước đã xong. Task chỉnh sửa giữ nguyên Input gốc của bước
/// (spec / output bước trước) và mang nhận xét ở <see cref="AgentTask.RevisionFeedback"/>; worker
/// chạy xong lại rơi về cổng duyệt của CHÍNH bước này (AdvanceLinearPipeline giữ CurrentStage).
///
/// Khác với Reject, chỉnh sửa được phép cả ở cổng POC: nhận xét ở đây là "POC chưa bám đúng spec
/// đã duyệt / cần chỉnh UI", KHÔNG phải đổi requirement (việc đó vẫn là của user qua chat BA).
///
/// Mỗi bước có trần <see cref="DeliveryPipeline.MaxRevisionRounds"/> vòng chỉnh sửa để kết quả
/// không hội tụ thì dừng lại thay vì đốt token vô hạn.
/// </summary>
public class RequestStageRevisionUseCase
{
    private readonly AppDbContext _db;

    public RequestStageRevisionUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <param name="includePocComments">
    /// Chỉ có nghĩa ở cổng POC: gom các ghi chú GHIM TRỰC TIẾP trên POC (status Open — xem
    /// <see cref="Domain.PocComment"/>) vào cuối nhận xét, kèm màn hình + selector của từng phần tử để
    /// Developer agent sửa đúng chỗ; ghi chú đã gom chuyển Sent để vòng chỉnh sửa sau không gửi lặp.
    /// Khi có ghi chú được gom, phần nhận xét gõ tay được phép trống.
    /// </param>
    /// <param name="onlyStage">
    /// Khi khác null, chỉ cho phép chỉnh sửa nếu run đang chờ ở ĐÚNG bước này (ngược lại trả
    /// <see cref="RequestStageRevisionResult.StageMismatch"/>). Đường vào từ Agent Dashboard để null (người
    /// duyệt được chỉnh mọi bước); đường vào từ trang POC Review của user thường truyền
    /// <see cref="WorkflowStageKey.PocPreview"/> — rào chắn để quyền "sửa demo" không nới thành quyền
    /// điều khiển các bước kỹ thuật phía sau.
    /// </param>
    /// <param name="pocCommentIds">
    /// Chỉ có nghĩa cùng <paramref name="includePocComments"/>: giới hạn ở ĐÚNG các ghi chú này thay vì mọi
    /// ghi chú Open. Đường vào từ trang POC Review truyền tập người dùng đã xếp vào nhóm "chỉnh bản demo"
    /// trong hộp xác nhận, để các ghi chú thuộc nhóm còn lại (gửi về Requirement) không bị cuốn theo. Để
    /// null ⇒ gom mọi ghi chú Open như cũ (cổng duyệt trên Agent Dashboard không có bước phân loại).
    /// </param>
    public async Task<RequestStageRevisionResult> ExecuteAsync(
        Guid projectId, string? feedback, Guid? runId = null, bool includePocComments = false,
        WorkflowStageKey? onlyStage = null, IReadOnlyCollection<Guid>? pocCommentIds = null)
    {
        feedback = feedback?.Trim() ?? string.Empty;

        var query = _db.WorkflowRuns
            .Where(x => x.ProjectId == projectId && x.Status == WorkflowRunStatus.WaitingForHuman);

        if (runId.HasValue)
            query = query.Where(x => x.Id == runId.Value);

        var run = await query.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        if (run == null)
            return RequestStageRevisionResult.NoWaitingRun;

        if (onlyStage.HasValue && run.CurrentStage != onlyStage.Value)
            return RequestStageRevisionResult.StageMismatch;

        // Khi chờ duyệt, CurrentStage là bước VỪA chạy xong — đó là bước cần chỉnh sửa.
        var step = DeliveryPipeline.Find(run.CurrentStage);
        if (step == null)
            return RequestStageRevisionResult.NoWaitingRun;

        // Cổng POC: gom các ghi chú ghim trực tiếp trên POC vào nhận xét. Đổi Status ở đây chỉ nằm trong
        // change tracker — các đường return sớm bên dưới (thiếu task/hết vòng) không SaveChanges nên
        // không "đốt" ghi chú của người dùng.
        // Các ghi chú vừa được gom, để nối RevisionTaskId khi biết id của task chỉnh sửa bên dưới —
        // bảng lịch sử dựa vào đó mở đúng bàn giao toàn văn của vòng đã xử lý ghi chú này.
        List<PocComment> sentPocComments = new();

        if (includePocComments && run.CurrentStage == WorkflowStageKey.PocPreview)
        {
            var pocCommentQuery = _db.PocComments
                .Where(c => c.ProjectId == projectId && c.Status == PocCommentStatus.Open);

            if (pocCommentIds != null)
            {
                var ids = pocCommentIds.Distinct().ToList();
                pocCommentQuery = pocCommentQuery.Where(c => ids.Contains(c.Id));
            }

            var pocComments = await pocCommentQuery
                .OrderBy(c => c.CreatedAt)
                .ToListAsync();

            if (pocComments.Count > 0)
            {
                feedback = AppendPocComments(feedback, pocComments);
                foreach (var comment in pocComments)
                {
                    comment.Status = PocCommentStatus.Sent;
                    comment.Route = PocCommentRoute.FixPoc;
                }

                sentPocComments = pocComments;
            }
        }

        if (feedback.Length == 0)
            return RequestStageRevisionResult.MissingFeedback;

        // Task gần nhất đã hoàn tất của bước này (bản gốc hoặc bản chỉnh sửa trước đó) — nguồn
        // Input nguyên bản và agent đảm nhiệm. Không có thì run đang ở trạng thái bất thường.
        var previousTask = await _db.AgentTasks
            .Where(t => t.WorkflowRunId == run.Id
                        && t.Type == step.TaskType
                        && t.Status == AgentTaskStatus.Completed)
            .OrderByDescending(t => t.FinishedAt ?? t.CreatedAt)
            .ThenByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (previousTask == null)
            return RequestStageRevisionResult.NoWaitingRun;

        var revisionsUsed = await _db.AgentTasks.CountAsync(
            t => t.WorkflowRunId == run.Id
                 && t.Type == step.TaskType
                 && t.RevisionFeedback != null);

        if (revisionsUsed >= DeliveryPipeline.MaxRevisionRounds)
            return RequestStageRevisionResult.RevisionLimitReached;

        var revisionTask = new AgentTask
        {
            WorkflowRunId = run.Id,
            ProjectId = projectId,
            AgentId = previousTask.AgentId,
            Type = step.TaskType,
            Status = AgentTaskStatus.Queued,
            Title = $"{step.Title} (chỉnh sửa lần {revisionsUsed + 1})",
            Input = previousTask.Input,
            RevisionFeedback = feedback
        };
        _db.AgentTasks.Add(revisionTask);

        foreach (var comment in sentPocComments)
            comment.RevisionTaskId = revisionTask.Id;

        // CurrentStage giữ nguyên — chạy xong bước này lại rơi về đúng cổng duyệt hiện tại.
        run.Status = WorkflowRunStatus.Queued;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Run đã bị một request song song chuyển khỏi WaitingForHuman (double-click / người khác
            // vừa Duyệt hoặc Từ chối) — không enqueue task chỉnh sửa trùng; ghi chú POC đổi Status
            // nằm cùng SaveChanges nên cũng không bị "đốt".
            return RequestStageRevisionResult.NoWaitingRun;
        }

        return RequestStageRevisionResult.Queued;
    }

    // Dựng khối ghi chú ghim máy-đọc-được nối vào nhận xét gõ tay: mỗi dòng một ghi chú, kèm màn hình
    // (data-view) và CSS selector để Developer agent tìm đúng phần tử trong poc-demo.html thay vì đoán.
    private static string AppendPocComments(string feedback, IReadOnlyList<PocComment> comments)
    {
        var sb = new System.Text.StringBuilder(feedback);

        if (sb.Length > 0)
            sb.Append("\n\n");

        sb.Append("## Ghi chú ghim trực tiếp trên POC (mỗi dòng một phần tử cần sửa)");
        for (var i = 0; i < comments.Count; i++)
        {
            var c = comments[i];
            sb.Append('\n').Append(i + 1).Append(". ");

            if (!string.IsNullOrWhiteSpace(c.PageView))
                sb.Append("[Màn hình \"").Append(c.PageView).Append("\"] ");
            if (!string.IsNullOrWhiteSpace(c.ElementLabel))
                sb.Append("Phần tử: ").Append(c.ElementLabel).Append(" — ");

            sb.Append(c.Comment);

            if (!string.IsNullOrWhiteSpace(c.ElementPath))
                sb.Append(" (selector: ").Append(c.ElementPath).Append(')');
        }

        return sb.ToString();
    }
}
