using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows;

/// <summary>
/// BackgroundService chạy các biến thể POC thử model ở trạng thái Queued (cùng mẫu poll như
/// <c>EvalRunWorker</c>). Tách khỏi <see cref="AgentTaskWorker"/> có chủ ý: worker đó là động cơ của
/// pipeline — mọi task nó nhặt đều dẫn tới cổng duyệt/bước kế — còn một lượt thử không thuộc run nào.
///
/// <para>
/// Chạy TUẦN TỰ, mỗi lần một biến thể trên toàn hệ thống: một lượt dựng POC là hàng chục lời gọi model
/// trả tiền thật, và <see cref="PocVariantRunner"/> chốt chi phí theo khung giờ chạy nên hai lượt chồng
/// nhau sẽ cộng lẫn tiền của nhau. Lúc khởi động, lượt còn Running là mồ côi sau restart ⇒ Failed (không
/// resume giữa chừng; thử lại là bấm lại).
/// </para>
/// </summary>
public class PocVariantWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PocVariantWorker> _logger;

    public PocVariantWorker(IServiceScopeFactory scopeFactory, ILogger<PocVariantWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverOrphanedAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover orphaned POC variants at startup.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while processing queued POC variants.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessNextAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var nextId = await db.PocVariants
            .Where(x => x.Status == PocVariantStatus.Queued)
            .OrderBy(x => x.CreatedAt)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (nextId is not Guid id)
            return;

        try
        {
            await scope.ServiceProvider.GetRequiredService<PocVariantRunner>().RunAsync(id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Runner tự ghi Failed cho lỗi của lượt chạy; tới đây là lỗi hạ tầng (DB…) — vẫn phải đánh dấu,
            // nếu không UI treo "Đang chạy" và hàng đợi nhặt lại đúng dòng này mãi.
            _logger.LogError(ex, "POC variant {VariantId} failed unexpectedly.", id);
            await MarkFailedAsync(id, ex.Message, cancellationToken);
        }
    }

    private async Task MarkFailedAsync(Guid id, string error, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var variant = await db.PocVariants.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (variant == null || variant.Status is PocVariantStatus.Completed or PocVariantStatus.Failed)
                return;

            variant.Status = PocVariantStatus.Failed;
            variant.Error = error;
            variant.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            scope.ServiceProvider.GetRequiredService<PocVariantProgress>().Clear(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark POC variant {VariantId} as failed.", id);
        }
    }

    private async Task RecoverOrphanedAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orphaned = await db.PocVariants.Where(x => x.Status == PocVariantStatus.Running).ToListAsync(cancellationToken);
        if (orphaned.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var variant in orphaned)
        {
            variant.Status = PocVariantStatus.Failed;
            variant.Error = "Lượt thử bị gián đoạn vì ứng dụng khởi động lại. Hãy chạy lại.";
            variant.FinishedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
