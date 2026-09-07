using ICOGenerator.Domain;

namespace ICOGenerator.Application.Models;

public record AiModelListPage(
    IReadOnlyList<AiModel> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
