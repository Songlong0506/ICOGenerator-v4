using System.Diagnostics;
using ICOGenerator.Services.Diagnostics;

namespace ICOGenerator.Application.Settings;

/// <summary>
/// Nút "Test Environment" ở trang Settings: chạy mọi <see cref="IEnvironmentCheck"/> rồi gom thành bảng cho
/// người vận hành đọc. Không đổi trạng thái app — chỉ ghi/xóa một file tạm để thử quyền ghi, và lần đầu trên máy
/// mới có thể tải Chromium (đúng việc audit POC đầu tiên cũng sẽ làm).
/// </summary>
public class CheckEnvironmentQuery
{
    private readonly EnvironmentDiagnostics _diagnostics;

    public CheckEnvironmentQuery(EnvironmentDiagnostics diagnostics) => _diagnostics = diagnostics;

    public async Task<EnvironmentReportVm> ExecuteAsync(bool requestIsHttps, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = await _diagnostics.RunAsync(new EnvironmentCheckContext(requestIsHttps), cancellationToken);
        stopwatch.Stop();

        return new EnvironmentReportVm
        {
            CheckedAtUtc = DateTime.UtcNow,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Passed = results.Count(x => x.Status == EnvironmentCheckStatus.Pass),
            Warnings = results.Count(x => x.Status == EnvironmentCheckStatus.Warning),
            Failed = results.Count(x => x.Status == EnvironmentCheckStatus.Fail),
            Items = results.Select(x => new EnvironmentCheckItemVm(
                x.Group, x.Name, x.Status.ToString().ToLowerInvariant(), x.Message, x.Fix, x.Detail)).ToList()
        };
    }
}
