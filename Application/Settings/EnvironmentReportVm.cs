namespace ICOGenerator.Application.Settings;

/// <summary>Kết quả nút "Test Environment" — trả JSON cho <c>settings-environment.js</c> vẽ bảng.</summary>
public sealed class EnvironmentReportVm
{
    public DateTime CheckedAtUtc { get; init; }
    public long DurationMs { get; init; }
    public int Passed { get; init; }
    public int Warnings { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<EnvironmentCheckItemVm> Items { get; init; } = Array.Empty<EnvironmentCheckItemVm>();
}

/// <param name="Status">"pass" | "warning" | "fail" | "info" — cũng là hậu tố class CSS của dòng.</param>
public sealed record EnvironmentCheckItemVm(string Group, string Name, string Status, string Message, string? Fix, string? Detail);
