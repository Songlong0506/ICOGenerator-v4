using System.Net.Http.Json;
using ICOGenerator.Configuration;

namespace ICOGenerator.Services.Organization;

/// <summary>
/// Typed HttpClient tới employee-api. BaseAddress và bearer token gắn ở DI
/// (<see cref="EmployeeApiTokenHandler"/>), nên ở đây chỉ còn path + giải mã JSON. ReadFromJsonAsync dùng
/// tuỳ chọn web (không phân biệt hoa/thường) nên API trả PascalCase hay camelCase đều đọc được.
/// </summary>
public sealed class EmployeeApiClient : IEmployeeApiClient
{
    private readonly HttpClient _http;
    private readonly EmployeeApiSettings _settings;

    public EmployeeApiClient(HttpClient http, EmployeeApiSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    public async Task<List<EmployeeInfoResponse>> GetAllAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(RequirePath(_settings.AllAssociatesPath, nameof(_settings.AllAssociatesPath)), ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<EmployeeInfoResponse>>(ct) ?? [];
    }

    public async Task<EmployeeInfoResponse?> GetLeavingInfoAsync(string personalNumber, CancellationToken ct = default)
    {
        var path = string.Format(
            RequirePath(_settings.LeavingInfoPath, nameof(_settings.LeavingInfoPath)),
            Uri.EscapeDataString(personalNumber));

        using var response = await _http.GetAsync(path, ct);

        // 4xx = API không có hồ sơ nghỉ việc của người này: bình thường, không phải lỗi của lần đồng bộ.
        if ((int)response.StatusCode is >= 400 and < 500)
            return null;

        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength == 0)
            return null;

        return await response.Content.ReadFromJsonAsync<EmployeeInfoResponse>(ct);
    }

    public async Task<List<OrgUnitInfoResponse>> GetAllOrgUnitsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(RequirePath(_settings.OrgUnitsPath, nameof(_settings.OrgUnitsPath)), ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<OrgUnitInfoResponse>>(ct) ?? [];
    }

    // BaseAddress chỉ được gắn khi EmployeeApi:BaseURL có giá trị (xem DI) — báo đúng key thiếu thay vì
    // để HttpClient ném "An invalid request URI was provided" khó hiểu.
    private string RequirePath(string path, string key)
    {
        if (_http.BaseAddress is null)
            throw new InvalidOperationException($"Thiếu cấu hình {EmployeeApiSettings.SectionName}:BaseURL.");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"Thiếu cấu hình {EmployeeApiSettings.SectionName}:{key}.");
        return path;
    }
}
