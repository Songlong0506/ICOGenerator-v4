using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Browser;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.WebPilot;

/// <summary>Dựng dữ liệu cho màn hình thử nghiệm WebPilot.</summary>
public class GetWebPilotPageQuery
{
    private readonly AppDbContext _db;
    private readonly WebPilotSettings _settings;
    private readonly WebPilotBrowser _browser;

    public GetWebPilotPageQuery(AppDbContext db, WebPilotSettings settings, WebPilotBrowser browser)
    {
        _db = db;
        _settings = settings;
        _browser = browser;
    }

    public async Task<WebPilotPageVm> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var agent = await _db.Agents
            .AsNoTracking()
            .Include(x => x.AiModel)
            .Where(x => x.RoleKey == AgentRoleKey.WebPilot)
            .Select(x => new { x.Id, ModelName = x.AiModel.ModelId })
            .FirstOrDefaultAsync(cancellationToken);

        return new WebPilotPageVm(
            Enabled: _settings.Enabled,
            AgentReady: agent != null,
            AgentModelName: agent?.ModelName,
            // Chưa lái lần nào thì coi như sẵn sàng: browser chỉ được khởi động ở lời gọi tool đầu tiên,
            // bắt người dùng chờ một lượt launch chỉ để tô một cái badge là đắt hơn thông tin thu được.
            BrowserReady: _browser.LastFailure == null,
            BrowserSkipReason: _browser.LastFailure,
            Headless: _settings.Headless,
            ProfileDir: _browser.ProfileDirectory,
            MaxSteps: _settings.MaxSteps);
    }
}
