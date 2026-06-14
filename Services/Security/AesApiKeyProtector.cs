using System.Security.Cryptography;
using System.Text;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Triển khai <see cref="IApiKeyProtector"/> bằng AES-GCM (mã hóa có xác thực).
/// Khóa lấy từ cấu hình <c>Encryption:ApiKeyKey</c>; mọi chuỗi đều hợp lệ vì được
/// băm SHA-256 thành khóa 256-bit. Định dạng lưu trữ: "enc:v1:" + base64(nonce|tag|ciphertext).
/// </summary>
public class AesApiKeyProtector : IApiKeyProtector
{
    private const string Prefix = "enc:v1:";
    private static readonly int NonceSize = AesGcm.NonceByteSizes.MaxSize; // 12 bytes
    private static readonly int TagSize = AesGcm.TagByteSizes.MaxSize;     // 16 bytes

    private readonly byte[] _key;

    public AesApiKeyProtector(IConfiguration configuration)
    {
        var secret = configuration["Encryption:ApiKeyKey"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "Thiếu cấu hình 'Encryption:ApiKeyKey'. Hãy đặt giá trị này trong appsettings.json, " +
                "biến môi trường Encryption__ApiKeyKey, hoặc user-secrets để mã hóa ApiKey.");
        }

        // Băm secret để luôn có khóa 256-bit hợp lệ bất kể độ dài chuỗi cấu hình.
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText ?? string.Empty;

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var combined = new byte[NonceSize + TagSize + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, combined, NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, combined, NonceSize + TagSize, cipherBytes.Length);

        return Prefix + Convert.ToBase64String(combined);
    }

    public string Unprotect(string? storedValue)
    {
        if (string.IsNullOrEmpty(storedValue))
            return storedValue ?? string.Empty;

        // Dữ liệu cũ chưa mã hóa (không có tiền tố) -> trả về nguyên trạng để tương thích ngược.
        if (!IsProtected(storedValue))
            return storedValue;

        try
        {
            var combined = Convert.FromBase64String(storedValue[Prefix.Length..]);
            if (combined.Length < NonceSize + TagSize)
                return storedValue;

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var cipherBytes = new byte[combined.Length - NonceSize - TagSize];

            Buffer.BlockCopy(combined, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(combined, NonceSize, tag, 0, TagSize);
            Buffer.BlockCopy(combined, NonceSize + TagSize, cipherBytes, 0, cipherBytes.Length);

            var plainBytes = new byte[cipherBytes.Length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            // Sai khóa hoặc dữ liệu hỏng — trả về nguyên trạng thay vì làm sập ứng dụng.
            return storedValue;
        }
        catch (FormatException)
        {
            return storedValue;
        }
    }
}
