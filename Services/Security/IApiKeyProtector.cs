namespace ICOGenerator.Services.Security;

/// <summary>
/// Mã hóa/giải mã các giá trị nhạy cảm (ví dụ ApiKey của AiModel) trước khi lưu xuống database.
/// </summary>
public interface IApiKeyProtector
{
    string Protect(string? plainText);

    /// <summary>Giải mã giá trị đã lưu. Giá trị plaintext cũ (chưa mã hóa) được trả về nguyên trạng.</summary>
    string Unprotect(string? storedValue);

    bool IsProtected(string? value);
}
