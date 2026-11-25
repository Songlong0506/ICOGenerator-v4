using System.Runtime.InteropServices;
using ICOGenerator.Localization;

namespace ICOGenerator.Services.Diagnostics.Checks;

/// <summary>
/// Thông tin nền để đọc mọi dòng phía dưới: app đang chạy dưới TÀI KHOẢN nào (trên IIS là danh tính của
/// application pool — PATH, cache Chromium, quyền thư mục, đăng nhập SQL bằng Windows đều tính theo tài khoản
/// này, không phải tài khoản admin đang RDP vào máy), môi trường nào, runtime nào.
/// </summary>
public sealed class HostEnvironmentCheck : IEnvironmentCheck
{
    private readonly IWebHostEnvironment _environment;

    public HostEnvironmentCheck(IWebHostEnvironment environment) => _environment = environment;

    public string Group => Loc.T("EnvCheck.Host.Group");
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<IReadOnlyList<EnvironmentCheckResult>> RunAsync(EnvironmentCheckContext context, CancellationToken cancellationToken)
    {
        var account = string.IsNullOrEmpty(Environment.UserDomainName)
            ? Environment.UserName
            : $@"{Environment.UserDomainName}\{Environment.UserName}";
        var underIis = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH"))
            || string.Equals(Environment.ProcessPath is { } p ? Path.GetFileNameWithoutExtension(p) : null, "w3wp", StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<EnvironmentCheckResult> results =
        [
            new(Group, Loc.T("EnvCheck.Host.Account"), EnvironmentCheckStatus.Info,
                underIis ? Loc.T("EnvCheck.Host.Account.Iis", account) : account),
            new(Group, Loc.T("EnvCheck.Host.Environment"), EnvironmentCheckStatus.Info,
                Loc.T("EnvCheck.Host.Environment.Value", _environment.EnvironmentName, Environment.MachineName)),
            new(Group, Loc.T("EnvCheck.Host.Runtime"), EnvironmentCheckStatus.Info,
                $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}"),
            new(Group, Loc.T("EnvCheck.Host.ContentRoot"), EnvironmentCheckStatus.Info, _environment.ContentRootPath),
        ];
        return Task.FromResult(results);
    }
}
