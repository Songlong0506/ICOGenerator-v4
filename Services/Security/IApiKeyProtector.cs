namespace ICOGenerator.Services.Security;

/// <summary>
/// Mã hóa/giải mã các giá trị nhạy cảm (ví dụ ApiKey của AiModel) trước khi lưu xuống database.
/// </summary>
public interface IApiKeyProtector
{
    /// <summary>Mã hóa plaintext thành chuỗi ciphertext có tiền tố để lưu trữ.</summary>
    string Protect(string? plainText);

    /// <summary>Giải mã giá trị đã lưu. Giá trị plaintext cũ (chưa mã hóa) được trả về nguyên trạng.</summary>
    string Unprotect(string? storedValue);

    /// <summary>Kiểm tra một giá trị đã được mã hóa hay chưa (dùng cho backfill dữ liệu cũ).</summary>
    bool IsProtected(string? value);
}
