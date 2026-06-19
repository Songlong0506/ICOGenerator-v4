using System.ComponentModel.DataAnnotations;
namespace ICOGenerator.Domain;
public class AiModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public string Name { get; set; } = string.Empty;
    [MaxLength(100)] public string Provider { get; set; } = "OpenAI-Compatible";
    [MaxLength(200)] public string ModelId { get; set; } = string.Empty;
    [MaxLength(500)] public string Endpoint { get; set; } = "http://127.0.0.1:1234/v1";
    // Lưu DB dạng đã mã hóa (xem AppDbContext + AesApiKeyProtector); cho phép 1000 ký tự vì ciphertext dài hơn plaintext.
    [MaxLength(1000)] public string ApiKey { get; set; } = "lm-studio";
    public int ContextWindow { get; set; } = 128000;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
