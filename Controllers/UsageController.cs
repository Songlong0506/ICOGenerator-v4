using ICOGenerator.Application.Usage;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

[RequirePermission(AppPermission.UsageView)]
public class UsageController : Controller
{
    private readonly GetUsageOverviewQuery _getUsageOverviewQuery;

    public UsageController(GetUsageOverviewQuery getUsageOverviewQuery)
    {
        _getUsageOverviewQuery = getUsageOverviewQuery;
    }

    public async Task<IActionResult> Index(int? year)
    {
        return View(await _getUsageOverviewQuery.ExecuteAsync(year));
    }
}
