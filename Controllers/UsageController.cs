using ICOGenerator.Application.Usage;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class UsageController : Controller
{
    private readonly GetUsageOverviewQuery _getUsageOverviewQuery;

    public UsageController(GetUsageOverviewQuery getUsageOverviewQuery)
    {
        _getUsageOverviewQuery = getUsageOverviewQuery;
    }

    public async Task<IActionResult> Index()
    {
        return View(await _getUsageOverviewQuery.ExecuteAsync());
    }
}
