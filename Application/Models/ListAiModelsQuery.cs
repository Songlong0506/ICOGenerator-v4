using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Models;

public class ListAiModelsQuery
{
    private readonly AppDbContext _db;
    public ListAiModelsQuery(AppDbContext db) => _db = db;

    public const int DefaultPageSize = 10;

    public async Task<AiModelListPage> ExecuteAsync(int page = 1, int pageSize = DefaultPageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;

        var baseQuery = _db.AiModels
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.Name);

        var totalCount = await baseQuery.CountAsync();

        var items = await baseQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new AiModelListPage(items, page, pageSize, totalCount);
    }
}
