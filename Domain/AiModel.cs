using System.ComponentModel.DataAnnotations;
namespace ICOGenerator.Domain;

public class AiModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public string Name { get; set; } = string.Empty;
    [MaxLength(100)] public string Provider { get; set; } = "OpenAI-Compatible";
    [MaxLength(200)] public string ModelId { get; set; } = string.Empty;
    [MaxLength(500)] public string Endpoint { get; set; }
    [MaxLength(1000)] public string ApiKey { get; set; }
    public int ContextWindow { get; set; } = 128000;
    public decimal InputPricePerMillionTokens { get; set; }
    public decimal OutputPricePerMillionTokens { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
