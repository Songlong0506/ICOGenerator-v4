using System.ComponentModel.DataAnnotations;

namespace ICOGenerator.Application.Projects;

public class ProjectCreateVm
{
    [Required] public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsUseBoschTemplate { get; set; } = true;
    [Required] public string BackendGitUrl { get; set; } = string.Empty;
    [Required] public string FrontendGitUrl { get; set; } = string.Empty;
}
