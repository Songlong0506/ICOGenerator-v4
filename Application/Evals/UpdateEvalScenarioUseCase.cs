using ICOGenerator.Data;
using ICOGenerator.Services.Evals;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

/// <summary>
/// Sửa một scenario của golden set (kể cả bật/tắt). Kết quả các run cũ KHÔNG bị sửa lại — chúng là
/// snapshot lịch sử; thay đổi chỉ ảnh hưởng run sau.
/// </summary>
public class UpdateEvalScenarioUseCase
{
    private readonly AppDbContext _db;
    private readonly EvalPromptCatalog _promptCatalog;

    public UpdateEvalScenarioUseCase(AppDbContext db, EvalPromptCatalog promptCatalog)
    {
        _db = db;
        _promptCatalog = promptCatalog;
    }

    public async Task<SaveEvalScenarioResult> ExecuteAsync(Guid id, string? name, string? promptKey, string? userInput, string? criteria, bool isActive, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(promptKey)
            || string.IsNullOrWhiteSpace(userInput) || string.IsNullOrWhiteSpace(criteria))
            return SaveEvalScenarioResult.InvalidInput;

        if (!_promptCatalog.Exists(promptKey))
            return SaveEvalScenarioResult.UnknownPromptKey;

        var scenario = await _db.EvalScenarios.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (scenario == null)
            return SaveEvalScenarioResult.NotFound;

        scenario.Name = name.Trim();
        scenario.PromptKey = promptKey.Trim();
        scenario.UserInput = userInput.Trim();
        scenario.Criteria = criteria.Trim();
        scenario.IsActive = isActive;
        scenario.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return SaveEvalScenarioResult.Saved;
    }
}
