namespace ICOGenerator.Application.Projects;

// Một dòng trong dropdown "Đơn vị yêu cầu" của modal New Project (render từ bảng OrgUnits).
// IsDepartment để view gom nhóm: phòng ban (department) đứng trước, orgUnit con phía sau.
public record OrgUnitOption(string Code, string Name, bool IsDepartment);
