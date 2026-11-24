using ICOGenerator.Domain;
using ICOGenerator.Localization;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Phần THIẾT KẾ của bước "Write Requirement": đưa bảng báo cáo, bản thiết kế UX và bảng phân quyền về khớp hội
/// thoại NGAY TRƯỚC khi soạn Product Brief — theo đúng thứ tự phụ thuộc <c>báo cáo → thiết kế → phân quyền</c>.
///
/// <para>
/// <b>Vì sao ở đây chứ không trong buổi phỏng vấn.</b> Thiết kế giữa buổi là thiết kế trên một phạm vi còn trôi:
/// mỗi lần người dùng kể thêm một việc là một lượt chắt phạm vi rồi một lượt thiết kế lại, và lượt chat ngay sau
/// bảng luồng phải chờ cả chuỗi danh mục → thiết kế → phản biện → phân quyền. Tới lúc bấm nút thì cổng readiness
/// đã xác nhận mọi nhóm áp dụng <c>[RÕ]</c>, nên một lần thiết kế đọc trọn hội thoại là đủ. Bấm lại mà hội thoại
/// không có lượt mới thì không gọi model lần nào (<see cref="UxDesignGate.IsStale"/>).
/// </para>
///
/// <para>
/// <b>Hai luật lỗi khác nhau, có chủ đích.</b> Thiết kế lỗi ⇒ <b>fail-closed</b>: Brief đọc mục "Các màn hình
/// chính" từ bản thiết kế, soạn khi không có nó là soạn một tài liệu thiếu màn hình — trả false để caller dừng,
/// người dùng bấm lại. Phân quyền lỗi ⇒ <b>fail-open</b>: Brief không đọc bảng phân quyền (nó chỉ vào
/// <c>## 6b</c> của AI Design Spec, sinh sau khi duyệt), nên vòng soạn đi tiếp và lần bấm sau soạn bù. Bảng báo
/// cáo cũng fail-open: nó vốn được ráp trong buổi phỏng vấn, ở đây chỉ là lượt ráp bù.
/// </para>
/// </summary>
public class DesignPreparationService
{
    private readonly ReportMapDraftService _reportMap;
    private readonly UxDesignService _uxDesign;
    private readonly PermissionMatrixDraftService _permissionMatrix;

    public DesignPreparationService(ReportMapDraftService reportMap, UxDesignService uxDesign, PermissionMatrixDraftService permissionMatrix)
    {
        _reportMap = reportMap;
        _uxDesign = uxDesign;
        _permissionMatrix = permissionMatrix;
    }

    /// <summary>
    /// Chạy ba lượt soạn theo thứ tự. Trả false khi bản thiết kế UX không dựng được — caller KHÔNG được soạn Brief.
    /// <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK.
    /// </summary>
    /// <param name="onProgress">Callback (kind, message, detail) báo tiến độ live, cùng hình dạng với vòng soạn Brief.</param>
    public async Task<bool> PrepareAsync(
        Project project, Agent ba, AiModel model,
        Action<string, string, string?>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        void Report(string kind, string message) => onProgress?.Invoke(kind, message, null);

        try
        {
            await _reportMap.UpdateAsync(project, ba, model, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nuốt có chủ ý: lời gọi model lỗi đã có log ở ILlmClient; thiết kế vẫn chạy trên bảng đang có.
        }

        if (UxDesignGate.IsStale(project, project.Conversations.Count))
            Report("tool", Loc.T("DesignPreparationService.Designing"));

        var design = await _uxDesign.PrepareAsync(project, ba, model, cancellationToken);
        if (design == UxDesignOutcome.Failed && !UxBlueprintBuilder.IsDesigned(project.UxBlueprint))
        {
            Report("error", Loc.T("DesignPreparationService.DesignFailed"));
            return false;
        }
        if (design == UxDesignOutcome.Failed)
        {
            // Đã có một bản thiết kế (lần bấm trước) mà lượt bổ sung lỗi: soạn Brief trên bản đó là soạn thiếu
            // đúng phần phạm vi vừa kể thêm — cùng luật fail-closed.
            Report("error", Loc.T("DesignPreparationService.RedesignFailed"));
            return false;
        }

        PermissionMatrixOutcome permissions;
        try
        {
            permissions = await _permissionMatrix.UpdateAsync(project, ba, model, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            permissions = PermissionMatrixOutcome.Failed;
        }

        if (permissions == PermissionMatrixOutcome.Failed)
            Report("info", Loc.T("DesignPreparationService.PermissionFailed"));
        else if (design == UxDesignOutcome.Designed || permissions != PermissionMatrixOutcome.Unchanged)
            Report("observation", Loc.T("DesignPreparationService.Done"));

        return true;
    }
}
