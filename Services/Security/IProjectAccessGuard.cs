using System.Security.Claims;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Kiểm tra quyền truy cập THEO TỪNG PROJECT (horizontal authorization), bổ sung cho
/// <see cref="IPermissionService"/> vốn chỉ trả lời "role này có quyền X không". Quy tắc
/// "User thường chỉ thấy project mình tạo" trước đây chỉ được áp ở danh sách Projects —
/// mọi endpoint nhận projectId/documentId vẫn mở cho bất kỳ ai đoán được GUID (lộ qua URL,
/// browser history, log, link trong notification). Guard này là một nguồn sự thật duy nhất
/// cho quy tắc đó, gọi ở đầu các controller action theo project.
///
/// Ngữ nghĩa: người có <see cref="Domain.Enums.AppPermission.ProjectsViewAll"/> (TeamDev/Admin
/// mặc định) luôn pass — không tốn thêm query DB nhờ cache của PermissionService. Người còn lại
/// chỉ pass khi project do CHÍNH họ tạo; project không tồn tại hoặc "không có chủ"
/// (CreatedByUsername null — tạo trước khi có tính năng) trả false, khớp hành vi ở danh sách.
/// Caller đối xử "không có quyền" y như "không tồn tại" (redirect về danh sách / 404) để không
/// xác nhận sự tồn tại của project cho người ngoài.
/// </summary>
public interface IProjectAccessGuard
{
    /// <summary>User có được thao tác trên project này không.</summary>
    Task<bool> CanAccessProjectAsync(ClaimsPrincipal user, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Truy cập theo id tài liệu sinh ra (ProjectDocument) — giải ngược về project sở hữu.</summary>
    Task<bool> CanAccessDocumentAsync(ClaimsPrincipal user, Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>Truy cập theo id một revision tài liệu — giải ngược document → project.</summary>
    Task<bool> CanAccessDocumentRevisionAsync(ClaimsPrincipal user, Guid revisionId, CancellationToken cancellationToken = default);

    /// <summary>Truy cập theo id tài liệu nguồn user upload (ProjectSourceFile).</summary>
    Task<bool> CanAccessSourceFileAsync(ClaimsPrincipal user, Guid sourceFileId, CancellationToken cancellationToken = default);

    /// <summary>Truy cập theo id một dòng log lời gọi model (AgentModelCallLog) — popup AI Call Logs.</summary>
    Task<bool> CanAccessCallLogAsync(ClaimsPrincipal user, Guid callLogId, CancellationToken cancellationToken = default);
}
