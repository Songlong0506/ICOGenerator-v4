using System.ComponentModel.DataAnnotations;
namespace ICOGenerator.Web.ViewModels;
public class ProjectCreateVm
{
    [Required] public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    [Required] public string GenerationMode { get; set; } = "BoschTemplate";
    [Required] public string BackendGitUrl { get; set; } = string.Empty;
    [Required] public string FrontendGitUrl { get; set; } = string.Empty;
}
