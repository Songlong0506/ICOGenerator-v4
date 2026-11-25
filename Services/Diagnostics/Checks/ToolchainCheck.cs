using System.Text.RegularExpressions;
using ICOGenerator.Data;
using ICOGenerator.Localization;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Diagnostics.Checks;

/// <summary>
/// Các lệnh mà app tự chạy trên máy chủ: <c>git</c> (clone repo dự án, bước Pull Request), <c>dotnet</c> +
/// <c>node</c>/<c>npm</c> (cổng biên dịch sau bước Implementation và tool RunCommand của agent), và Claude Code
/// CLI khi có model <c>claude-code://</c>. Tất cả tìm theo PATH của TÀI KHOẢN CHẠY APP — cài Node "cho riêng
/// tôi" bằng tài khoản admin thì application pool không thấy.
/// </summary>
public sealed partial class ToolchainCheck : IEnvironmentCheck
{
    // Git cũ hơn không đọc được credential của dự án (GIT_CONFIG_COUNT) — xem docs/operations.md.
    private static readonly Version MinGit = new(2, 31);

    private readonly AppDbContext _db;
    private readonly LlmSettings _llmSettings;

    public ToolchainCheck(AppDbContext db, LlmSettings llmSettings)
    {
        _db = db;
        _llmSettings = llmSettings;
    }

    public string Group => Loc.T("EnvCheck.Tools.Group");
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyList<EnvironmentCheckResult>> RunAsync(EnvironmentCheckContext context, CancellationToken cancellationToken)
    {
        var git = GitAsync(cancellationToken);
        var dotnet = DotnetAsync(cancellationToken);
        var node = SimpleAsync("node", ["--version"], cancellationToken);
        var npm = SimpleAsync("npm", ["--version"], cancellationToken);
        var claude = ClaudeAsync(cancellationToken);

        var results = new List<EnvironmentCheckResult>();
        results.AddRange(await git);
        results.Add(await dotnet);
        results.Add(await node);
        results.Add(await npm);
        if (await claude is { } claudeRow)
            results.Add(claudeRow);
        return results;
    }

    private async Task<IReadOnlyList<EnvironmentCheckResult>> GitAsync(CancellationToken ct)
    {
        var version = await ProcessProbe.RunAsync("git", ["--version"], ct);
        if (!version.Started || version.ExitCode != 0)
            return [new(Group, "git", EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Tools.NotFound", "git"),
                Loc.T("EnvCheck.Tools.Git.Fix"), version.Output)];

        var results = new List<EnvironmentCheckResult>();
        var parsed = ParseVersion(version.Output);
        results.Add(parsed is not null && parsed < MinGit
            ? new(Group, "git", EnvironmentCheckStatus.Warning, Loc.T("EnvCheck.Tools.Git.Old", version.Output, MinGit),
                Loc.T("EnvCheck.Tools.Git.Fix"))
            : new(Group, "git", EnvironmentCheckStatus.Pass, version.Output));

        var userName = await ProcessProbe.RunAsync("git", ["config", "--get", "user.name"], ct);
        var userEmail = await ProcessProbe.RunAsync("git", ["config", "--get", "user.email"], ct);
        var identity = Loc.T("EnvCheck.Tools.Git.Identity");
        results.Add(userName.ExitCode == 0 && userEmail.ExitCode == 0
            ? new(Group, identity, EnvironmentCheckStatus.Pass, $"{userName.Output} <{userEmail.Output}>")
            : new(Group, identity, EnvironmentCheckStatus.Warning, Loc.T("EnvCheck.Tools.Git.Identity.Missing"),
                Loc.T("EnvCheck.Tools.Git.Identity.Fix")));
        return results;
    }

    private async Task<EnvironmentCheckResult> DotnetAsync(CancellationToken ct)
    {
        var name = Loc.T("EnvCheck.Tools.DotnetSdk");
        var sdks = await ProcessProbe.RunAsync("dotnet", ["--list-sdks"], ct);
        if (!sdks.Started)
            return new(Group, name, EnvironmentCheckStatus.Warning, Loc.T("EnvCheck.Tools.NotFound", "dotnet"),
                Loc.T("EnvCheck.Tools.Dotnet.Fix"), sdks.Output);

        var lines = sdks.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var has8 = lines.Any(l => ParseVersion(l) is { Major: >= 8 });
        return has8
            ? new(Group, name, EnvironmentCheckStatus.Pass, string.Join(" · ", lines.Select(l => l.Split(' ')[0])))
            : new(Group, name, EnvironmentCheckStatus.Warning, Loc.T("EnvCheck.Tools.Dotnet.NoSdk"),
                Loc.T("EnvCheck.Tools.Dotnet.Fix"), sdks.Output);
    }

    private async Task<EnvironmentCheckResult> SimpleAsync(string command, string[] args, CancellationToken ct)
    {
        var result = await ProcessProbe.RunShimAsync(command, args, ct);
        return result.Started && result.ExitCode == 0
            ? new(Group, command, EnvironmentCheckStatus.Pass, ProcessProbe.FirstLine(result.Output))
            : new(Group, command, EnvironmentCheckStatus.Warning, Loc.T("EnvCheck.Tools.NotFound", command),
                Loc.T("EnvCheck.Tools.Node.Fix"), result.Output);
    }

    // Chỉ kiểm khi thật sự có model chạy qua CLI — máy không dùng thì đừng bày một dòng đỏ vô nghĩa.
    private async Task<EnvironmentCheckResult?> ClaudeAsync(CancellationToken ct)
    {
        var endpoints = await _db.AiModels.AsNoTracking().Where(x => x.IsActive).Select(x => x.Endpoint).ToListAsync(ct);
        if (!endpoints.Any(ClaudeCodeCli.IsClaudeCodeEndpoint))
            return null;

        var name = "Claude Code CLI";
        var binary = ClaudeCodeCli.FindBinary(_llmSettings.ClaudeCodeBinaryPath);
        if (binary is null)
            return new(Group, name, EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Tools.Claude.Missing"), Loc.T("EnvCheck.Tools.Claude.Fix"));

        var version = await ProcessProbe.RunAsync(binary, ["--version"], ct);
        return version.Started && version.ExitCode == 0
            ? new(Group, name, EnvironmentCheckStatus.Pass, $"{ProcessProbe.FirstLine(version.Output)} · {binary}")
            : new(Group, name, EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Tools.Claude.Broken", binary),
                Loc.T("EnvCheck.Tools.Claude.Fix"), version.Output);
    }

    internal static Version? ParseVersion(string text)
    {
        var match = VersionPattern().Match(text);
        return match.Success && Version.TryParse(match.Value, out var v) ? v : null;
    }

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex VersionPattern();
}
