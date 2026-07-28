using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;

namespace ICOGenerator.Application.Evals;

/// <summary>Thêm một scenario vào golden set. PromptKey phải là template .md có thật dưới /Prompts.</summary>
public class CreateEvalScenarioUseCase
{
    private readonly AppDbContext _db;
    private readonly PromptFileCatalog _promptCatalog;

    public CreateEvalScenarioUseCase(AppDbContext db, PromptFileCatalog promptCatalog)
    {
        _db = db;
        _promptCatalog = promptCatalog;
    }

    /// <param name="kind">Prompt = một lượt (mặc định); Interview = phỏng vấn mô phỏng nhiều lượt, khi đó
    /// <paramref name="userInput"/> là HỒ SƠ PERSONA của người dùng giả lập.</param>
    public async Task<SaveEvalScenarioResult> ExecuteAsync(string? name, string? promptKey, string? userInput, string? criteria, string? createdByUsername, EvalScenarioKind kind = EvalScenarioKind.Prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(promptKey)
            || string.IsNullOrWhiteSpace(userInput) || string.IsNullOrWhiteSpace(criteria))
            return SaveEvalScenarioResult.InvalidInput;

        if (!_promptCatalog.Exists(promptKey))
            return SaveEvalScenarioResult.UnknownPromptKey;

        _db.EvalScenarios.Add(new EvalScenario
        {
            Name = name.Trim(),
            PromptKey = promptKey.Trim(),
            UserInput = userInput.Trim(),
            Criteria = criteria.Trim(),
            Kind = kind,
            CreatedByUsername = createdByUsername
        });
        await _db.SaveChangesAsync(cancellationToken);

        return SaveEvalScenarioResult.Saved;
    }
}
