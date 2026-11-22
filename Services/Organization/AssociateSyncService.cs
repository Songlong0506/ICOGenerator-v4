using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ICOGenerator.Services.Organization;

/// <summary>
/// Đồng bộ bảng <c>Associates</c> từ employee-api, khớp theo <c>PersonalNumber</c>, rồi điền
/// <c>LeavingDate</c> cho người đã rời danh sách. Cột API không trả (<c>IsIndirect</c>, <c>Birthday</c>)
/// giữ nguyên giá trị đang có.
/// </summary>
public sealed class AssociateSyncService
{
    private readonly AppDbContext _db;
    private readonly IEmployeeApiClient _employeeApi;
    private readonly ILogger<AssociateSyncService> _logger;

    public AssociateSyncService(AppDbContext db, IEmployeeApiClient employeeApi, ILogger<AssociateSyncService> logger)
    {
        _db = db;
        _employeeApi = employeeApi;
        _logger = logger;
    }

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var employees = await _employeeApi.GetAllAsync(ct);

        // Cùng lý do như OrgUnitSyncService: danh sách rỗng là API hỏng, không phải cả nhà máy nghỉ việc.
        if (employees.Count == 0)
            throw new InvalidOperationException("employee-api trả về 0 nhân sự; huỷ lần đồng bộ.");

        var employeesByNumber = employees
            .Where(employee => !string.IsNullOrWhiteSpace(employee.PersonalNumber))
            .GroupBy(employee => employee.PersonalNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var associates = await _db.Associates.ToListAsync(ct);
        var associatesByNumber = associates
            .Where(associate => !string.IsNullOrWhiteSpace(associate.PersonalNumber))
            .GroupBy(associate => associate.PersonalNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        int inserted = 0, updated = 0, deleted = 0;

        foreach (var (personalNumber, employee) in employeesByNumber)
        {
            if (!associatesByNumber.TryGetValue(personalNumber, out var associate))
            {
                var created = new Associate
                {
                    Id = Guid.NewGuid(),
                    PersonalNumber = personalNumber,
                    DisplayName = personalNumber,
                    CreatedDate = now,
                    CreatedBy = OrganizationSyncJob.Actor,
                };
                Apply(employee, created);
                _db.Associates.Add(created);
                inserted++;
                continue;
            }

            var changed = Apply(employee, associate);

            // Người đã rời danh sách nay quay lại (tái tuyển dụng): bật lại và bỏ ngày nghỉ cũ, nếu không
            // họ bị loại khỏi tìm kiếm/headcount vĩnh viễn dù đang làm việc.
            if (associate.IsDelete)
            {
                associate.IsDelete = false;
                associate.LeavingDate = null;
                changed = true;
            }

            if (!changed)
                continue;

            associate.UpdatedDate = now;
            associate.UpdatedBy = OrganizationSyncJob.Actor;
            updated++;
        }

        foreach (var associate in associates)
        {
            if (associate.IsDelete
                || (!string.IsNullOrWhiteSpace(associate.PersonalNumber) && employeesByNumber.ContainsKey(associate.PersonalNumber.Trim())))
                continue;

            associate.IsDelete = true;
            associate.UpdatedDate = now;
            associate.UpdatedBy = OrganizationSyncJob.Actor;
            deleted++;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Associate sync: {Inserted} inserted, {Updated} updated, {Deleted} deleted",
            inserted, updated, deleted);

        return new SyncResult(inserted, updated, deleted);
    }

    /// <summary>
    /// Điền <c>LeavingDate</c> cho người đã bị xoá mềm mà chưa có ngày nghỉ — mỗi người một lời gọi
    /// leaving-info. Người API chưa có thông tin sẽ được hỏi lại ở lần đồng bộ sau. Trả về số người đã điền.
    /// </summary>
    public async Task<int> SyncLeavingDatesAsync(CancellationToken ct = default)
    {
        var leavers = await _db.Associates
            .Where(associate => associate.IsDelete && associate.LeavingDate == null && associate.PersonalNumber != null)
            .ToListAsync(ct);

        var filled = 0;
        var failed = 0;
        Exception? lastError = null;

        foreach (var associate in leavers)
        {
            EmployeeInfoResponse? info;
            try
            {
                info = await _employeeApi.GetLeavingInfoAsync(associate.PersonalNumber!.Trim(), ct);
            }
            catch (HttpRequestException ex)
            {
                // Một người lỗi không được chặn những người còn lại; họ sẽ được hỏi lại lần sau.
                failed++;
                lastError = ex;
                _logger.LogWarning(ex, "Leaving-info lookup failed for associate {PersonalNumber}", associate.PersonalNumber);
                continue;
            }

            if (info?.LeavingDate is not { } leavingDate)
                continue;

            associate.LeavingDate = leavingDate;
            associate.UpdatedDate = DateTime.UtcNow;
            associate.UpdatedBy = OrganizationSyncJob.Actor;
            filled++;
        }

        if (filled > 0)
            await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Associate leaving dates: {Filled}/{Total} filled, {Failed} lookups failed",
            filled, leavers.Count, failed);

        // Hỏng TOÀN BỘ (API chết, sai quyền…) thì cho job thất bại để Hangfire hiện lỗi và thử lại,
        // thay vì một dòng log Warning lẫn trong hàng trăm dòng khác.
        if (leavers.Count > 0 && failed == leavers.Count)
            throw new InvalidOperationException($"Mọi lời gọi leaving-info ({failed}) đều lỗi.", lastError);

        return filled;
    }

    private static bool Apply(EmployeeInfoResponse employee, Associate associate)
    {
        var displayName = OrgUnitSyncService.Normalize(employee.EmployeeName) ?? associate.DisplayName;
        var gender = employee.Gender is { } value ? value.ToString() : associate.Gender;
        var globalId = OrgUnitSyncService.Normalize(employee.GlobalId);
        var userId = OrgUnitSyncService.Normalize(employee.UserId);
        var orgUnitCode = OrgUnitSyncService.Normalize(employee.OrgUnitId);
        var organizationUnit = OrgUnitSyncService.Normalize(employee.OrganizationUnit);
        var email = OrgUnitSyncService.Normalize(employee.Email);
        var position = OrgUnitSyncService.Normalize(employee.Position);
        var costcenter = OrgUnitSyncService.Normalize(employee.Costcenter);

        var changed = associate.DisplayName != displayName
            || associate.Gender != gender
            || associate.GlobalId != globalId
            || associate.UserId != userId
            || associate.OrgUnitCode != orgUnitCode
            || associate.OrganizationUnit != organizationUnit
            || associate.Email != email
            || associate.Position != position
            || associate.Costcenter != costcenter
            || associate.HiredDate != employee.HiredDate;

        if (!changed)
            return false;

        associate.DisplayName = displayName;
        associate.Gender = gender;
        associate.GlobalId = globalId;
        associate.UserId = userId;
        associate.OrgUnitCode = orgUnitCode;
        associate.OrganizationUnit = organizationUnit;
        associate.Email = email;
        associate.Position = position;
        associate.Costcenter = costcenter;
        associate.HiredDate = employee.HiredDate;
        return true;
    }
}
