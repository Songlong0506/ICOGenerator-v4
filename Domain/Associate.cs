using System.ComponentModel.DataAnnotations;

namespace ICOGenerator.Domain;

public class Associate
{
    public Guid Id { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? UpdatedDate { get; set; }

    public string? UpdatedBy { get; set; }

    public bool IsDelete { get; set; }

    [MaxLength(50)]
    public string? PersonalNumber { get; set; }

    [MaxLength(50)]
    public string? GlobalId { get; set; }

    [MaxLength(255)]
    public string? DisplayName { get; set; }

    [MaxLength(50)]
    public string? OrgUnitCode { get; set; }

    [MaxLength(255)]
    public string? OrganizationUnit { get; set; }

    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? Mobiphone { get; set; }

    public string? PickupAddress { get; set; }

    [MaxLength(10)]
    public string? Gender { get; set; }

    [MaxLength(255)]
    public string? Position { get; set; }

    public decimal StandardWorkingHour { get; set; }

    [MaxLength(50)]
    public string? Costcenter { get; set; }

    [MaxLength(50)]
    public string? LeadingPerson { get; set; }

    [MaxLength(100)]
    public string? EmployeeSubGroup { get; set; }

    public DateTime? HiredDate { get; set; }

    [MaxLength(100)]
    public string? UserId { get; set; }

    public string? CreatedBy { get; set; }

    public bool IsIndirect { get; set; }

    public DateTime? LeavingDate { get; set; }

    public DateTime? Birthday { get; set; }
}
