namespace ICOGenerator.Services.Organization;

/// <summary>Lời gọi tới employee-api. Tách interface để test đồng bộ không cần API thật.</summary>
public interface IEmployeeApiClient
{
    Task<List<EmployeeInfoResponse>> GetAllAsync(CancellationToken ct = default);

    /// <summary>null khi API không có thông tin nghỉ việc của người này (4xx hoặc body rỗng).</summary>
    Task<EmployeeInfoResponse?> GetLeavingInfoAsync(string personalNumber, CancellationToken ct = default);

    Task<List<OrgUnitInfoResponse>> GetAllOrgUnitsAsync(CancellationToken ct = default);
}
