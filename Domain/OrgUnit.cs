using System.ComponentModel.DataAnnotations;

namespace ICOGenerator.Domain;

public class OrgUnit
{
    public Guid Id { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? UpdatedDate { get; set; }

    public string? UpdatedBy { get; set; }

    public bool IsDelete { get; set; }

    [MaxLength(255)]
    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    [MaxLength(50)]
    public string? CostCenter { get; set; }

    [MaxLength(50)]
    public string? DiscManagerLId { get; set; }

    public string? DisciplinaryResponsible { get; set; }

    [MaxLength(50)]
    public string? OrgUnitCode { get; set; }

    [MaxLength(50)]
    public string? TargetResponsible { get; set; }

    [MaxLength(50)]
    public string? TrgtManagerLId { get; set; }

    [MaxLength(10)]
    public string? TypeOrganize { get; set; }

    public bool IsDepartment { get; set; }

    public string? CreatedBy { get; set; }
}
