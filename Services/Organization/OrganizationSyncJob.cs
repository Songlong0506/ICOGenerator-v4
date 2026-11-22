using Hangfire;
using ICOGenerator.Configuration;
using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ICOGenerator.Services.Organization;

/// <summary>
/// Job Hangfire đồng bộ dữ liệu tổ chức từ employee-api, chạy mỗi ngày theo <c>EmployeeApi:SyncCron</c>.
/// Thứ tự cố định: OrgUnits → Associates → LeavingDate. Mỗi bước tự lưu riêng nên bước sau lỗi không
/// làm mất bước trước; Hangfire thử lại cả job, và cả ba bước đều chạy lại được an toàn.
///
/// <para>
/// Cây tổ chức và bối cảnh BA đang cache 1 giờ (<see cref="OrgChartProvider"/>,
/// <c>OrganizationContextService</c>), nên dữ liệu mới tới BA chậm nhất 1 giờ sau khi job xong.
/// </para>
/// </summary>
public sealed class OrganizationSyncJob
{
    /// <summary>Id của recurring job trên dashboard Hangfire.</summary>
    public const string RecurringJobId = "organization-sync";

    /// <summary>Giá trị ghi vào CreatedBy/UpdatedBy của bản ghi do job này tạo/sửa.</summary>
    public const string Actor = "employee-api-sync";

    private readonly OrgUnitSyncService _orgUnits;
    private readonly AssociateSyncService _associates;

    public OrganizationSyncJob(OrgUnitSyncService orgUnits, AssociateSyncService associates)
    {
        _orgUnits = orgUnits;
        _associates = associates;
    }

    // DisableConcurrentExecution: lần chạy bù lúc khởi động có thể trùng giờ với lịch hằng ngày — hai lần
    // chạy song song sẽ cùng chèn một người mới hai lần.
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(CancellationToken ct)
    {
        await _orgUnits.SyncAsync(ct);
        await _associates.SyncAsync(ct);
        await _associates.SyncLeavingDatesAsync(ct);
    }

    /// <summary>
    /// Đăng ký (hoặc gỡ, khi <c>EmployeeApi:Enabled = false</c>) lịch chạy hằng ngày. DB còn trống (cài mới)
    /// thì xếp thêm một lần chạy ngay, để BA và màn hình tạo project không phải chờ tới lịch kế tiếp mới
    /// có danh sách orgUnit. Gọi một lần lúc khởi động, sau <c>DbInitializer</c>.
    /// </summary>
    public static async Task ScheduleAsync(IServiceProvider services)
    {
        var settings = services.GetRequiredService<EmployeeApiSettings>();
        var recurringJobs = services.GetRequiredService<IRecurringJobManager>();
        var logger = services.GetRequiredService<ILogger<OrganizationSyncJob>>();

        if (!settings.Enabled)
        {
            recurringJobs.RemoveIfExists(RecurringJobId);
            logger.LogInformation("Organization sync is disabled (EmployeeApi:Enabled = false)");
            return;
        }

        recurringJobs.AddOrUpdate<OrganizationSyncJob>(
            RecurringJobId,
            job => job.RunAsync(CancellationToken.None),
            settings.SyncCron,
            new RecurringJobOptions { TimeZone = ResolveTimeZone(settings.SyncTimeZone) });

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.OrgUnits.AnyAsync() && await db.Associates.AnyAsync())
            return;

        services.GetRequiredService<IBackgroundJobClient>()
            .Enqueue<OrganizationSyncJob>(job => job.RunAsync(CancellationToken.None));
        logger.LogInformation("OrgUnits/Associates is empty — queued an immediate organization sync");
    }

    /// <summary>
    /// Nhận cả ID IANA lẫn ID Windows: máy Windows thiếu ICU chỉ biết ID Windows, Linux chỉ biết IANA, nên
    /// thử nguyên văn rồi thử bản quy đổi. Sai cả hai ⇒ ném lỗi lúc khởi động, không lặng lẽ chạy theo UTC.
    /// </summary>
    internal static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            return zone;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId)
            && TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out zone))
            return zone;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId)
            && TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out zone))
            return zone;

        throw new InvalidOperationException($"{EmployeeApiSettings.SectionName}:SyncTimeZone '{id}' không phải múi giờ hợp lệ.");
    }
}
