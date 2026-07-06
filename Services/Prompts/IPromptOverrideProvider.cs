namespace ICOGenerator.Services.Prompts;

/// <summary>Bản prompt DB đang active của một template: đủ metadata để nơi dùng ghi lại "đã chạy phiên bản nào".</summary>
public sealed record PromptOverride(Guid Id, int VersionNumber, string Content);

/// <summary>
/// Nguồn nội dung prompt GHI ĐÈ từ DB (bảng PromptTemplateVersions — màn hình Prompt Studio):
/// template có bản active ⇒ trả bản đó, PromptTemplateService dùng THAY nội dung file; không có ⇒
/// trả null, nội dung file trong repo được dùng như trước. Tách interface để PromptTemplateService
/// (vốn chỉ đọc file tĩnh) không phụ thuộc cứng DbContext và test stub được dễ dàng.
/// </summary>
public interface IPromptOverrideProvider
{
    /// <summary>Bản active của một template; null = dùng file. KHÔNG ném lỗi (fail-open về file).</summary>
    PromptOverride? GetActiveOverride(string promptKey);

    /// <summary>Xóa cache sau khi lưu/kích hoạt/gỡ một phiên bản để thay đổi có hiệu lực ngay.</summary>
    void Invalidate();
}
