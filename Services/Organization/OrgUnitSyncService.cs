using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ICOGenerator.Services.Organization;

/// <summary>Số bản ghi một lần đồng bộ đã thêm / sửa / xoá mềm.</summary>
public sealed record SyncResult(int Inserted, int Updated, int Deleted);

/// <summary>
/// Đồng bộ bảng <c>OrgUnits</c> từ employee-api, khớp theo <c>OrgUnitCode</c>. Đơn vị không còn trong API
/// thì xoá mềm (<c>IsDelete</c>), xuất hiện lại thì bật lại — không xoá cứng vì
/// <c>Project.OrgUnitCode</c> vẫn tra sang đơn vị cũ để giữ nhãn lịch sử.
/// </summary>
public sealed class OrgUnitSyncService
{
    private readonly AppDbContext _db;
    private readonly IEmployeeApiClient _employeeApi;
    private readonly ILogger<OrgUnitSyncService> _logger;

    public OrgUnitSyncService(AppDbContext db, IEmployeeApiClient employeeApi, ILogger<OrgUnitSyncService> logger)
    {
        _db = db;
        _employeeApi = employeeApi;
        _logger = logger;
    }

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var sources = await _employeeApi.GetAllOrgUnitsAsync(ct);

        // API trả rỗng gần như chắc chắn là API hỏng chứ không phải nhà máy giải thể: đi tiếp là xoá mềm
        // cả bảng, và BA mất toàn bộ bối cảnh tổ chức cho tới lần đồng bộ sau.
        if (sources.Count == 0)
            throw new InvalidOperationException("employee-api trả về 0 orgUnit; huỷ lần đồng bộ.");

        var sourcesByCode = sources
            .Where(source => !string.IsNullOrWhiteSpace(source.OrgUnitId))
            .GroupBy(source => source.OrgUnitId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var orgUnits = await _db.OrgUnits.ToListAsync(ct);
        var orgUnitsByCode = orgUnits
            .Where(orgUnit => !string.IsNullOrWhiteSpace(orgUnit.OrgUnitCode))
            .GroupBy(orgUnit => orgUnit.OrgUnitCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        int inserted = 0, updated = 0, deleted = 0;

        foreach (var (code, source) in sourcesByCode)
        {
            if (!orgUnitsByCode.TryGetValue(code, out var orgUnit))
            {
                var created = new OrgUnit
                {
                    Id = Guid.NewGuid(),
                    OrgUnitCode = code,
                    DisplayName = code,
                    CreatedDate = now,
                    CreatedBy = OrganizationSyncJob.Actor,
                };
                Apply(source, created);
                _db.OrgUnits.Add(created);
                inserted++;
                continue;
            }

            var changed = Apply(source, orgUnit);
            if (orgUnit.IsDelete)
            {
                orgUnit.IsDelete = false;
                changed = true;
            }

            if (!changed)
                continue;

            orgUnit.UpdatedDate = now;
            orgUnit.UpdatedBy = OrganizationSyncJob.Actor;
            updated++;
        }

        foreach (var orgUnit in orgUnits)
        {
            if (orgUnit.IsDelete
                || (!string.IsNullOrWhiteSpace(orgUnit.OrgUnitCode) && sourcesByCode.ContainsKey(orgUnit.OrgUnitCode.Trim())))
                continue;

            orgUnit.IsDelete = true;
            orgUnit.UpdatedDate = now;
            orgUnit.UpdatedBy = OrganizationSyncJob.Actor;
            deleted++;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "OrgUnit sync: {Inserted} inserted, {Updated} updated, {Deleted} deleted",
            inserted, updated, deleted);

        return new SyncResult(inserted, updated, deleted);
    }

    private static bool Apply(OrgUnitInfoResponse source, OrgUnit orgUnit)
    {
        var displayName = Normalize(source.Name) ?? orgUnit.DisplayName;
        var costCenter = Normalize(source.CostCenter);
        var targetResponsible = Normalize(source.TargetResponsible);
        var managerId = Normalize(source.TrgtManagerLId);
        var typeOrganize = Normalize(source.TypeOrganize);

        var changed = orgUnit.DisplayName != displayName
            || orgUnit.CostCenter != costCenter
            || orgUnit.TargetResponsible != targetResponsible
            || orgUnit.TrgtManagerLId != managerId
            || orgUnit.TypeOrganize != typeOrganize;

        if (!changed)
            return false;

        orgUnit.DisplayName = displayName;
        orgUnit.CostCenter = costCenter;
        orgUnit.TargetResponsible = targetResponsible;
        orgUnit.TrgtManagerLId = managerId;
        orgUnit.TypeOrganize = typeOrganize;
        return true;
    }

    internal static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
