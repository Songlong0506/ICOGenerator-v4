using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Models;

public class ListAiModelsQuery
{
    private readonly AppDbContext _db;
    public ListAiModelsQuery(AppDbContext db) => _db = db;

    public Task<List<AiModel>> ExecuteAsync() => _db.AiModels
        .OrderByDescending(x => x.IsDefault)
        .ThenBy(x => x.Name)
        .ToListAsync();
}
