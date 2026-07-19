namespace ICOGenerator.Contracts.Requirements;

// Kết quả phân loại miền nghiệp vụ của dự án (Prompts/BusinessAnalyst/project-domain.v1.md):
// một slug thuộc taxonomy cố định của ProjectDomainClassifier, hoặc "other".
public class ProjectDomainResult
{
    public string DomainKey { get; set; } = "";
}
