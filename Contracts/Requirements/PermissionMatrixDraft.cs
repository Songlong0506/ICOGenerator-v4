namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả của lượt SOẠN BẢNG PHÂN QUYỀN (<c>PermissionMatrixDraftService</c>): mỗi chức năng của mỗi màn
/// hình một <see cref="PermissionMatrixRow"/>. Không đi thẳng vào DB: <c>PermissionMatrixBuilder.Build</c>
/// kéo nó về đúng phạm vi màn hình đã chốt và bơm đủ vai trò vào mọi dòng rồi mới lưu.
/// </summary>
public class PermissionMatrixDraft
{
    public List<PermissionMatrixRow> Rows { get; set; } = new();
}
