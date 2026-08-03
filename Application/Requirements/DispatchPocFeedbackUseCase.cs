using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum PocFeedbackDispatchStatus
{
    Ok,
    /// <summary>Không chọn ghi chú nào ở cả hai nhóm.</summary>
    NothingSelected,
    ProjectNotFound,
    /// <summary>
    /// Danh sách gửi lên không khớp thực tế: id lạ, id trùng ở cả hai nhóm, hoặc ghi chú đã rời trạng thái
    /// Open trong lúc hộp xác nhận còn mở (người khác vừa gửi/xóa). Client phải nạp lại rồi phân loại lại.
    /// </summary>
    InvalidSelection,
    /// <summary>Đường sửa tài liệu hỏng — KHÔNG ghi chú nào đổi trạng thái (xem <see cref="PocFeedbackDispatchReport.RequirementError"/>).</summary>
    RequirementFailed,
    /// <summary>Đường chỉnh bản demo hỏng — KHÔNG ghi chú nào đổi trạng thái (xem <see cref="PocFeedbackDispatchReport.FixError"/>).</summary>
    FixFailed
}

/// <param name="HeldCount">
/// Số ghi chú "chỉnh bản demo" được GIỮ NGUYÊN trạng thái Open thay vì gửi Dev ngay, vì lượt này đã đi
/// đường sửa tài liệu (xem phần precedence trong <see cref="DispatchPocFeedbackUseCase"/>).
/// </param>
public record PocFeedbackDispatchReport(
    PocFeedbackDispatchStatus Status,
    int RoutedCount = 0,
    int FixSentCount = 0,
    int HeldCount = 0,
    RoutePocFeedbackResult? RequirementError = null,
    RequestStageRevisionResult? FixError = null);

/// <summary>
/// MỘT lượt gửi ghi chú POC đi xử lý, sau khi người dùng đã soát bảng phân loại của
/// <see cref="TriagePocFeedbackUseCase"/> và xác nhận nhóm cho từng ghi chú.
///
/// Vì sao gộp: trang POC Review trước đây có hai nút riêng, mà CẢ HAI đều nuốt trọn mọi ghi chú Open —
/// không nút nào nhận được tập con. Một buổi review lẫn cả hai loại (chuyện thường) vì thế không có nút
/// nào đúng: bấm "nhờ Dev" thì tài liệu giữ nguyên chỗ hiểu sai và mọi bước sau kế thừa nó; bấm "gửi về
/// Requirement" thì các ghi chú thẩm mỹ bị đánh dấu RoutedToRequirement dù BA đã loại chúng khỏi tin nhắn,
/// và ReopenPocCommentUseCase không mở lại được — mất trắng. Ở đây mỗi đường chỉ nhận ĐÚNG tập con của nó.
///
/// PRECEDENCE khi cả hai nhóm đều có ghi chú: chạy đường TÀI LIỆU, và GIỮ NGUYÊN Open các ghi chú thuộc
/// nhóm chỉnh demo. Lý do: sửa tài liệu sẽ soạn lại Brief/Spec rồi dựng POC MỚI từ đầu, nên một vòng vá
/// HTML lên bản demo sắp bị thay là vừa phí một lượt trong trần
/// <see cref="DeliveryPipeline.MaxRevisionRounds"/>, vừa cho ra bản vá bị bỏ đi ngay sau đó. Giữ Open thì
/// các ghi chú ấy còn nguyên cho vòng review của POC mới — không mất gì, chỉ hoãn lại.
///
/// Mọi nhánh lỗi đều KHÔNG để lại trạng thái nửa vời: chỉ một trong hai đường chạy trong mỗi lượt, và
/// đường nào hỏng thì hỏng trước khi đổi trạng thái ghi chú.
/// </summary>
public class DispatchPocFeedbackUseCase
{
    private readonly AppDbContext _db;
    private readonly RoutePocFeedbackToRequirementUseCase _route;
    private readonly RequestStageRevisionUseCase _revision;

    public DispatchPocFeedbackUseCase(
        AppDbContext db,
        RoutePocFeedbackToRequirementUseCase route,
        RequestStageRevisionUseCase revision)
    {
        _db = db;
        _route = route;
        _revision = revision;
    }

    /// <param name="fixIds">Ghi chú người dùng xếp vào "nhờ đội Dev chỉnh bản demo".</param>
    /// <param name="requirementIds">Ghi chú người dùng xếp vào "tài liệu yêu cầu hiểu sai".</param>
    public async Task<PocFeedbackDispatchReport> ExecuteAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> fixIds,
        IReadOnlyCollection<Guid> requirementIds,
        CancellationToken cancellationToken = default)
    {
        var projectExists = await _db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, cancellationToken);
        if (!projectExists)
            return new PocFeedbackDispatchReport(PocFeedbackDispatchStatus.ProjectNotFound);

        var fix = fixIds.Distinct().ToList();
        var requirement = requirementIds.Distinct().ToList();

        if (fix.Count == 0 && requirement.Count == 0)
            return new PocFeedbackDispatchReport(PocFeedbackDispatchStatus.NothingSelected);

        if (fix.Intersect(requirement).Any())
            return new PocFeedbackDispatchReport(PocFeedbackDispatchStatus.InvalidSelection);

        // Mọi id phải là ghi chú Open của ĐÚNG project này. Chặn cả id lạ lẫn ghi chú vừa bị một lượt khác
        // xử lý trong lúc hộp xác nhận còn mở — gửi tiếp lúc đó là gửi theo một bảng phân loại đã cũ.
        var selected = fix.Concat(requirement).ToList();
        var openCount = await _db.PocComments.AsNoTracking()
            .CountAsync(c => c.ProjectId == projectId
                             && c.Status == PocCommentStatus.Open
                             && selected.Contains(c.Id), cancellationToken);
        if (openCount != selected.Count)
            return new PocFeedbackDispatchReport(PocFeedbackDispatchStatus.InvalidSelection);

        if (requirement.Count > 0)
        {
            var routed = await _route.ExecuteAsync(projectId, requirement, cancellationToken);
            if (routed != RoutePocFeedbackResult.Ok)
                return new PocFeedbackDispatchReport(PocFeedbackDispatchStatus.RequirementFailed, RequirementError: routed);

            return new PocFeedbackDispatchReport(
                PocFeedbackDispatchStatus.Ok, RoutedCount: requirement.Count, HeldCount: fix.Count);
        }

        var revision = await _revision.ExecuteAsync(
            projectId, feedback: null, runId: null, includePocComments: true,
            onlyStage: WorkflowStageKey.PocPreview, pocCommentIds: fix);

        if (revision != RequestStageRevisionResult.Queued)
            return new PocFeedbackDispatchReport(PocFeedbackDispatchStatus.FixFailed, FixError: revision);

        return new PocFeedbackDispatchReport(PocFeedbackDispatchStatus.Ok, FixSentCount: fix.Count);
    }
}
