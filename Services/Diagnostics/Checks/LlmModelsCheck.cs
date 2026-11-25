using ICOGenerator.Data;
using ICOGenerator.Localization;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Diagnostics.Checks;

/// <summary>
/// Gọi thử TỪNG model đang bật bằng đúng <see cref="IModelConnectionTester"/> của nút "Test Connection" ở trang
/// Models — cùng đường dây (proxy theo endpoint, shim tương thích), không ghi call log, không tính budget; một
/// lời gọi "ping" tốn vài token. Lỗi hay gặp trên máy chủ: proxy công ty chặn, sai ApiKey sau khi đổi khóa mã hóa.
/// </summary>
public sealed class LlmModelsCheck : IEnvironmentCheck
{
    private const int MaxModels = 10;

    private readonly AppDbContext _db;
    private readonly IModelConnectionTester _tester;
    private readonly LlmSettings _settings;

    public LlmModelsCheck(AppDbContext db, IModelConnectionTester tester, LlmSettings settings)
    {
        _db = db;
        _tester = tester;
        _settings = settings;
    }

    public string Group => Loc.T("EnvCheck.Llm.Group");
    public TimeSpan Timeout => TimeSpan.FromSeconds(_settings.TestConnectionTimeoutSeconds + 15);

    public async Task<IReadOnlyList<EnvironmentCheckResult>> RunAsync(EnvironmentCheckContext context, CancellationToken cancellationToken)
    {
        var results = new List<EnvironmentCheckResult>
        {
            new(Group, Loc.T("EnvCheck.Llm.Proxy"), EnvironmentCheckStatus.Info,
                _settings.ProxyEnabled ? Loc.T("EnvCheck.Llm.Proxy.On", _settings.ProxyAddress) : Loc.T("EnvCheck.Llm.Proxy.Off"))
        };

        var models = await _db.AiModels.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.ModelId)
            .Take(MaxModels)
            .ToListAsync(cancellationToken);

        if (models.Count == 0)
        {
            results.Add(new(Group, Loc.T("EnvCheck.Llm.Models"), EnvironmentCheckStatus.Fail,
                Loc.T("EnvCheck.Llm.None"), Loc.T("EnvCheck.Llm.None.Fix")));
            return results;
        }

        var outcomes = await Task.WhenAll(models.Select(m => _tester.TestAsync(m, cancellationToken)));
        for (var i = 0; i < models.Count; i++)
        {
            var label = models[i].DisplayLabel();
            var outcome = outcomes[i];
            results.Add(outcome.IsSuccess
                ? new(Group, label, EnvironmentCheckStatus.Pass, Loc.T("EnvCheck.Llm.Ok", outcome.DurationMs))
                : new(Group, label, EnvironmentCheckStatus.Fail, outcome.ErrorMessage ?? Loc.T("EnvCheck.Llm.Failed"),
                    Loc.T("EnvCheck.Llm.Failed.Fix"), outcome.Detail));
        }

        return results;
    }
}
