using System.ComponentModel.DataAnnotations;

namespace ICOGenerator.Domain;

/// <summary>
/// Một đơn vị trong bản sao cây tổ chức đồng bộ từ HR_Portal (seed ở <see cref="Data.OrgUnitsSeedData"/>).
/// Ba đường đọc dùng bảng này: dropdown "Đơn vị yêu cầu" của màn Projects, roll-up phòng ban của màn
/// Usage, và bức tranh tổ chức trong prompt BA.
///
/// <para>
/// Chỉ giữ những cột ba đường đọc đó dùng tới; các cột HR còn lại (cost center, người chịu trách nhiệm
/// kỷ luật, loại hình tổ chức, dấu vết created/updated...) đã bỏ vì không nơi nào đọc. Thêm cột mới thì
/// thêm cùng lúc với đường đọc dùng nó, và bổ sung khoá tương ứng vào <c>Data/SeedData/org-units.ndjson</c>.
/// </para>
/// </summary>
public class OrgUnit
{
    public Guid Id { get; set; }

    /// <summary>Xoá mềm từ HR_Portal — mọi truy vấn đều lọc <c>!IsDelete</c>.</summary>
    public bool IsDelete { get; set; }

    /// <summary>Tên đơn vị (vd "HcP/TEF3.3").</summary>
    [MaxLength(255)]
    public string? DisplayName { get; set; }

    /// <summary>Mã đơn vị — khoá tra cứu chính, cũng là thứ <see cref="Project.OrgUnitCode"/> lưu lại.</summary>
    [MaxLength(50)]
    public string? OrgUnitCode { get; set; }

    /// <summary>Mã đơn vị CẤP TRÊN trực tiếp — cạnh nối của cây tổ chức (roll-up về phòng ban).</summary>
    [MaxLength(50)]
    public string? TargetResponsible { get; set; }

    /// <summary>Mã nhân sự của quản lý đơn vị, tra sang <see cref="Associate.PersonalNumber"/>.</summary>
    [MaxLength(50)]
    public string? TrgtManagerLId { get; set; }

    /// <summary>Đơn vị này là một phòng ban (department) chứ không phải nhóm con.</summary>
    public bool IsDepartment { get; set; }
}
