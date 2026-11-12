namespace ICOGenerator.Domain.Enums;

/// <summary>
/// "Vỏ agent" dựng một biến thể POC (xem <see cref="PocVariant.Harness"/>). Lưu DB dạng chuỗi ⇒ không đổi
/// tên giá trị đã có dữ liệu.
/// </summary>
public enum PocVariantHarness
{
    /// <summary>Đường dựng của chính app — một lượt sinh + vài lượt sửa, rơi về agent nhiều bước (như bản chính).</summary>
    Builtin,

    /// <summary>Agent CLI opencode chạy ngoài app, gọi model qua relay của app — xem <c>PocOpencodeBuilder</c>.</summary>
    Opencode
}
