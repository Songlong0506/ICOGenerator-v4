namespace ICOGenerator.Services.Organization;

/// <summary>Một nhân sự trả về từ employee-api (danh sách <c>all-lv1</c> và <c>leaving-info</c>).</summary>
public class EmployeeInfoResponse
{
    public string? PersonalNumber { get; set; }

    public string? GlobalId { get; set; }

    public string? UserId { get; set; }

    public string? EmployeeName { get; set; }

    public string? OrgUnitId { get; set; }

    public string? OrganizationUnit { get; set; }

    public string? Email { get; set; }

    /// <summary>1 = nam, 2 = nữ. Lưu vào <c>Associate.Gender</c> dưới dạng chuỗi ("1"/"2").</summary>
    public int? Gender { get; set; }

    public string? Position { get; set; }

    public string? Costcenter { get; set; }

    public DateTime? HiredDate { get; set; }

    public DateTime? LeavingDate { get; set; }
}

/// <summary>Một orgUnit trả về từ employee-api (<c>api/Departments</c>).</summary>
public class OrgUnitInfoResponse
{
    public string? OrgUnitId { get; set; }

    public string? Name { get; set; }

    public string? CostCenter { get; set; }

    public string? TargetResponsible { get; set; }

    public string? TrgtManagerLId { get; set; }

    /// <summary>A/L = department — xem <see cref="OrgChart.IsDepartmentType"/>.</summary>
    public string? TypeOrganize { get; set; }
}
