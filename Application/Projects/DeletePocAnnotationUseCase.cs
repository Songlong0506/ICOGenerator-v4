using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum DeletePocAnnotationResult
{
    Deleted,
    NotFound,

    /// <summary>Chỉ tác giả xóa được annotation của mình, và chỉ khi còn Open (chưa gửi đội Dev/agent).</summary>
    NotAllowed
}

public class DeletePocAnnotationUseCase
{
    private readonly AppDbContext _db;

    public DeletePocAnnotationUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<DeletePocAnnotationResult> ExecuteAsync(Guid annotationId, string? actorUsername, CancellationToken cancellationToken = default)
    {
        var annotation = await _db.PocAnnotations.FirstOrDefaultAsync(a => a.Id == annotationId, cancellationToken);
        if (annotation == null)
            return DeletePocAnnotationResult.NotFound;

        if (annotation.Status != PocAnnotationStatus.Open || annotation.AuthorUsername != actorUsername)
            return DeletePocAnnotationResult.NotAllowed;

        _db.PocAnnotations.Remove(annotation);
        await _db.SaveChangesAsync(cancellationToken);
        return DeletePocAnnotationResult.Deleted;
    }
}
