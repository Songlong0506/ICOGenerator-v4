using ICOGenerator.Services.Organization;

namespace ICOGenerator.Tests.Organization;

/// <summary>employee-api giả: trả đúng danh sách test đặt sẵn, ghi lại các PersonalNumber đã hỏi leaving-info.</summary>
public sealed class FakeEmployeeApiClient : IEmployeeApiClient
{
    public List<EmployeeInfoResponse> Employees { get; set; } = [];
    public List<OrgUnitInfoResponse> OrgUnits { get; set; } = [];
    public Dictionary<string, EmployeeInfoResponse?> LeavingInfo { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FailingLeavingInfo { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> LeavingInfoRequests { get; } = [];

    public Task<List<EmployeeInfoResponse>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(Employees);

    public Task<List<OrgUnitInfoResponse>> GetAllOrgUnitsAsync(CancellationToken ct = default) => Task.FromResult(OrgUnits);

    public Task<EmployeeInfoResponse?> GetLeavingInfoAsync(string personalNumber, CancellationToken ct = default)
    {
        LeavingInfoRequests.Add(personalNumber);
        if (FailingLeavingInfo.Contains(personalNumber))
            throw new HttpRequestException("boom");
        return Task.FromResult(LeavingInfo.TryGetValue(personalNumber, out var info) ? info : null);
    }
}
