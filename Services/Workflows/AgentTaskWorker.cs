using System.Collections.Concurrent;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Notifications;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows;

public class AgentTaskWorker : BackgroundService
{
    // Trần cứng cho Workers:MaxConcurrentAgentTasks — mỗi task là một agent run dài (nhiều lời gọi
    // LLM), chạy quá nhiều task song song chỉ dồn nghẽn về endpoint model.
    private const int MaxConfigurableConcurrency = 16;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentTaskWorker> _logger;
    private readonly IWorkflowProgressReporter _progress;
    private readonly IWebHostEnvironment _environment;
    private readonly int _maxConcurrentTasks;

    // Task đang bay (taskId → Task xử lý) và project đang bận. Dispatcher (vòng ExecuteAsync) là người
    // ghi/dọn duy nhất của _inFlight; _busyProjects được chính task xử lý gỡ trong finally khi xong —
    // ConcurrentDictionary vì hai việc đó xảy ra trên thread khác nhau.
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();
    private readonly ConcurrentDictionary<Guid, byte> _busyProjects = new();

    public AgentTaskWorker(IServiceScopeFactory scopeFactory, ILogger<AgentTaskWorker> logger, IWorkflowProgressReporter progress, IWebHostEnvironment environment, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _progress = progress;
        _environment = environment;
        // Mặc định 1 = giữ nguyên hành vi tuần tự cũ (an toàn cho LLM tự host một model). Tăng lên khi
        // endpoint model chịu được nhiều request song song để các PROJECT khác nhau không phải xếp hàng
        // chờ nhau cả một bước Implementation dài — trong MỘT project task vẫn luôn chạy tuần tự.
        _maxConcurrentTasks = Math.Clamp(configuration.GetValue("Workers:MaxConcurrentAgentTasks", 1), 1, MaxConfigurableConcurrency);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchQueuedTasksAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while dispatching queued workflow agent tasks.");
            }

            // Catch the shutdown cancellation here instead of letting it escape
            // ExecuteAsync (which the host treats as a crash); shutdown becomes a clean exit.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Shutdown: chờ các task đang bay unwind (chúng quan sát stoppingToken nên dừng nhanh). Task bị
        // ngắt giữa chừng để nguyên Running — reaper của DbInitializer re-queue ở lần khởi động sau,
        // đúng ngữ nghĩa cũ khi worker tuần tự bị ngắt.
        try
        {
            await Task.WhenAll(_inFlight.Values.ToArray());
        }
        catch
        {
            // Mỗi task tự xử lý/log lỗi của nó; ở đây chỉ cần không để exception thoát khỏi ExecuteAsync.
        }
    }

    // Nhặt các task Queued (cũ nhất trước) và giao cho tối đa MaxConcurrentAgentTasks task chạy SONG
    // SONG — nhưng mỗi project chỉ một task tại một thời điểm: hai run của cùng project (vd hai lần bấm
    // "Write Requirement" liên tiếp) ghi lên cùng workspace, chạy chồng sẽ giẫm file của nhau.
    private async Task DispatchQueuedTasksAsync(CancellationToken stoppingToken)
    {
        // Dọn entry đã xong (dispatcher là người ghi duy nhất của _inFlight nên dọn ở đây là đủ).
        foreach (var entry in _inFlight)
            if (entry.Value.IsCompleted)
                _inFlight.TryRemove(entry.Key, out _);

        var slots = _maxConcurrentTasks - _inFlight.Count;
        if (slots <= 0)
            return;

        var candidates = await SelectCandidatesAsync(slots, stoppingToken);

        foreach (var candidate in candidates)
        {
            // Giữ chỗ project TRƯỚC khi spawn để nhịp dispatch sau không chọn thêm task cùng project.
            if (!_busyProjects.TryAdd(candidate.ProjectId, 0))
                continue;

            _inFlight[candidate.TaskId] = RunOneAsync(candidate.TaskId, candidate.ProjectId, stoppingToken);
        }
    }

    private sealed record QueuedCandidate(Guid TaskId, Guid ProjectId);

    private async Task<IReadOnlyList<QueuedCandidate>> SelectCandidatesAsync(int slots, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var busyProjects = _busyProjects.Keys.ToArray();

        // Lấy dư một chút (slots × 4) rồi mới gom một-task-mỗi-project trong RAM: một project dồn nhiều
        // task Queued liên tiếp sẽ không chiếm hết danh sách ứng viên của các project khác.
        var rows = await db.AgentTasks
            .AsNoTracking()
            .Where(x => x.Status == AgentTaskStatus.Queued && !busyProjects.Contains(x.ProjectId))
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { x.Id, x.ProjectId })
            .Take(slots * 4)
            .ToListAsync(cancellationToken);

        return rows
            .DistinctBy(x => x.ProjectId)
            .Take(slots)
            .Select(x => new QueuedCandidate(x.Id, x.ProjectId))
            .ToList();
    }

    // Chạy TRỌN một task: tự xử lý mọi lỗi (không bao giờ ném ra ngoài — dispatcher không await nó tại
    // chỗ) và luôn trả "chỗ" project khi xong để nhịp dispatch sau nhặt tiếp task kế của project đó.
    private async Task RunOneAsync(Guid taskId, Guid projectId, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessClaimedTaskAsync(taskId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // App shutting down mid-task: leave it Running and let the startup reaper re-queue it on the
            // next launch, instead of recording a misleading Failed for work that was merely interrupted.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed while processing workflow agent task {TaskId}.", taskId);
        }
        finally
        {
            _busyProjects.TryRemove(projectId, out _);
        }
    }

    private async Task ProcessClaimedTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var agentRunService = scope.ServiceProvider.GetRequiredService<AgentRunService>();
        var notifier = scope.ServiceProvider.GetRequiredService<INotificationService>();

        // Chỉ nhận task còn Queued — giữa lúc dispatcher chọn và lúc này, task có thể đã bị xử lý nơi khác.
        var task = await db.AgentTasks
            .Include(x => x.WorkflowRun)
            .FirstOrDefaultAsync(x => x.Id == taskId && x.Status == AgentTaskStatus.Queued, cancellationToken);

        if (task == null)
            return;

        if (task.AgentId == null)
        {
            _progress.Report(task.WorkflowRunId, "error", "Không có agent nào được gán cho task này.");
            task.Status = AgentTaskStatus.Failed;
            task.Error = "No agent is assigned to this task.";
            task.FinishedAt = DateTime.UtcNow;
            FailRun(task.WorkflowRun);
            await notifier.NotifyRunFailedAsync(task.WorkflowRun, task.Error, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        // CLAIM nguyên tử Queued → Running qua concurrency token (AgentTask.Status): một dispatch khác
        // (hoặc instance app khác trong tương lai) đã nhặt trước thì bên thua lặng lẽ bỏ qua — không bao
        // giờ hai bên cùng chạy một task. Claim nằm NGOÀI khối try bên dưới để thua claim không bị đánh
        // nhầm thành task Failed.
        task.Status = AgentTaskStatus.Running;
        task.StartedAt = DateTime.UtcNow;
        task.Attempt += 1;
        task.WorkflowRun.Status = WorkflowRunStatus.Running;
        task.WorkflowRun.StartedAt ??= DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return;
        }

        try
        {
            _progress.Report(task.WorkflowRunId, "start", $"Bắt đầu task: {task.Title}" + (task.Attempt > 1 ? $" (lần thử {task.Attempt})" : ""));

            if (task.Type == AgentTaskType.RequirementAnalysis)
            {
                var outcome = await RunRequirementDraftAsync(scope, task, cancellationToken);

                task.Status = AgentTaskStatus.Completed;
                task.Output = outcome == RequirementDraftOutcome.NeedsMoreInfo
                    ? RequirementDraftMarkers.NeedsMoreInfo
                    : "Requirement documents generated/updated.";
                task.FinishedAt = DateTime.UtcNow;
                task.WorkflowRun.Status = WorkflowRunStatus.Completed;
                task.WorkflowRun.CurrentStage = WorkflowStageKey.Completed;
                task.WorkflowRun.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            // Sinh AI Design Spec (sau khi Approve) chạy NỀN ở đây thay vì đồng bộ trong ApproveRequirementUseCase
            // (vốn làm treo màn hình chờ LLM). Run này một bước của BA; xong thì TỰ khởi động delivery workflow
            // dựng POC với nội dung spec vừa sinh — giữ nguyên hành vi cũ, chỉ khác là không treo UI.
            if (task.Type == AgentTaskType.AiDesignSpec)
            {
                var specContent = await RunAiDesignSpecAsync(scope, task, cancellationToken);

                task.Status = AgentTaskStatus.Completed;
                task.Output = "AI Design Spec generated.";
                task.FinishedAt = DateTime.UtcNow;
                task.WorkflowRun.Status = WorkflowRunStatus.Completed;
                task.WorkflowRun.CurrentStage = WorkflowStageKey.Completed;
                task.WorkflowRun.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);

                // Spec đã sinh xong → tự khởi động delivery workflow dựng POC (giống bước cũ trong Approve).
                // Tạo run delivery TRƯỚC khi báo "completed" để khi UI reload (run Requirement hoàn tất),
                // panel delivery đã tồn tại và bắt đầu hiển thị tiến độ. task.Input giữ versionName.
                var orchestrator = scope.ServiceProvider.GetRequiredService<IWorkflowOrchestrator>();
                await orchestrator.StartDeliveryWorkflowAsync(task.ProjectId, task.Input, specContent);

                _progress.Report(task.WorkflowRunId, "completed", "Đã sinh AI Design Spec — đang khởi động quy trình dựng POC…");
                return;
            }

            // Tài liệu kỹ thuật là BƯỚC 2 của Delivery Pipeline (sau POC): chạy qua RequirementDocsService
            // (sinh BRD/SRS/FSD/UserStories từ Product Brief + AI Design Spec) thay vì agent+prompt chung,
            // rồi rơi về cổng duyệt tuyến tính như mọi bước — KHÔNG hoàn tất run.
            if (task.Type == AgentTaskType.TechnicalDocs)
            {
                await RunTechnicalDocsAsync(scope, task, cancellationToken);

                task.Status = AgentTaskStatus.Completed;
                task.Output = "Technical documents generated.";
                task.FinishedAt = DateTime.UtcNow;
                await AdvanceLinearPipelineAsync(notifier, task, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            // POC template chỉ cần cho bước POC preview.
            if (task.Type == AgentTaskType.PocPreview)
            {
                // Task CHỈNH SỬA giữ nguyên POC hiện có để agent sửa theo nhận xét — re-seed ở đây
                // sẽ ghi đè poc-demo.html về placeholder và xoá sạch sản phẩm lần trước.
                if (task.RevisionFeedback == null)
                {
                    _progress.Report(task.WorkflowRunId, "setup", "Chuẩn bị workspace và template POC…");
                    await EnsureDesignAssetsAsync(scope, db, task.ProjectId);
                }

                // Đưa AI Design Spec của run này (task.Input) cho AuditPocContent qua WorkspaceTools
                // CÙNG SCOPE (agentRunService và tool registry cùng resolve instance scoped này), để
                // audit đối chiếu ĐỘ PHỦ so với spec — màn hình thiếu, business rule chưa chạy —
                // thay vì chỉ soát wiring nội bộ rồi "OK" trên một POC mới phủ nửa spec.
                scope.ServiceProvider.GetRequiredService<WorkspaceTools>().SetPocSpec(task.Input);
            }

            var project = await db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken)
                ?? throw new InvalidOperationException($"Không tìm thấy project {task.ProjectId} cho task này.");

            // Generation Mode do TeamDev chọn ở Agent Dashboard. Cổng Approve đã chặn pipeline đi quá POC
            // khi giá trị còn null, nên tới các bước Architecture/Implementation nó luôn đã được chốt;
            // null ở đây chỉ là phòng thủ và được coi như "không dùng Bosch" để cả seeding lẫn prompt nhất quán.
            var useBoschTemplate = project.IsUseBoschTemplate == true;

            // Bosch template: clone bộ khung chuẩn (.NET + Angular) vào workspace TRƯỚC khi Developer
            // hiện thực, để bước Implementation code THÊM vào skeleton thay vì dựng khung từ đầu.
            if (task.Type == AgentTaskType.Implementation && useBoschTemplate)
            {
                _progress.Report(task.WorkflowRunId, "setup", "Bosch template: chuẩn bị skeleton (.NET + Angular)…");
                var seeder = scope.ServiceProvider.GetRequiredService<BoschTemplateSeeder>();
                var skeletonKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
                var seedSummary = await seeder.SeedAsync(skeletonKey, cancellationToken);
                _progress.Report(task.WorkflowRunId, "setup", $"Bosch skeleton: {seedSummary}");
            }

            // Task chỉnh sửa: kèm bàn giao của lần gần nhất vào prompt để agent biết mình đã làm gì
            // (sản phẩm thật vẫn nằm trong workspace; đây chỉ là phần tóm tắt bàn giao).
            var previousOutput = task.RevisionFeedback == null
                ? null
                : await db.AgentTasks
                    .Where(t => t.WorkflowRunId == task.WorkflowRunId
                                && t.Type == task.Type
                                && t.Status == AgentTaskStatus.Completed
                                && t.Id != task.Id)
                    .OrderByDescending(t => t.FinishedAt ?? t.CreatedAt)
                    .ThenByDescending(t => t.CreatedAt)
                    .Select(t => t.Output)
                    .FirstOrDefaultAsync(cancellationToken);

            var promptBuilder = scope.ServiceProvider.GetRequiredService<WorkflowTaskPromptBuilder>();
            var prompt = promptBuilder.Build(task.Type, task.Input, useBoschTemplate, task.RevisionFeedback, previousOutput);
            var maxSteps = DeliveryPipeline.Find(task.WorkflowRun.CurrentStage)?.MaxSteps ?? 6;

            // POC được dựng qua NHIỀU call (SetPocContent cho màn hình đầu, rồi các AppendPocContent cho
            // phần còn lại) để không call nào phải chứa cả trang và bị cắt do giới hạn token. Agent tự nối
            // hết các phần rồi kết thúc; mỗi call ghi thẳng ra đĩa nên phần đã dựng vẫn được giữ kể cả khi
            // chạm giới hạn bước.
            var output = await agentRunService.RunAsync(
                task.ProjectId,
                task.AgentId.Value,
                prompt,
                maxSteps,
                onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail),
                onToken: token => _progress.ReportToken(task.WorkflowRunId, token),
                workflowRunId: task.WorkflowRunId,
                cancellationToken: cancellationToken);

            // Nếu agent đạt giới hạn số bước mà chưa hoàn tất (chưa gọi tool tạo deliverable),
            // coi là thất bại để báo lỗi rõ thay vì ghi nhận Completed sai lệch.
            if (string.Equals(output, AgentRunService.MaxStepsReachedResult, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    task.Type == AgentTaskType.PocPreview
                        ? "Agent đạt giới hạn số bước mà chưa tạo được POC (chưa gọi SetPocContent thành công)."
                        : $"Agent đạt giới hạn số bước mà chưa hoàn tất bước '{task.Title}' (chưa gọi tool ghi kết quả).");

            task.Status = AgentTaskStatus.Completed;
            task.Output = output;
            task.FinishedAt = DateTime.UtcNow;

            // Vòng tự sửa lỗi (Testing↔BugFix) là một CHU TRÌNH (không phải hand-off tuyến tính) nên
            // được xử lý riêng. Nếu task này không thuộc chu trình đó thì rơi về cổng duyệt tuyến tính.
            if (!await TryAdvanceTestFixCycleAsync(db, task, notifier, cancellationToken))
                await AdvanceLinearPipelineAsync(notifier, task, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // App shutting down mid-task: leave it Running and let the startup reaper re-queue it on the
            // next launch, instead of recording a misleading Failed for work that was merely interrupted.
            throw;
        }
        catch (Exception ex)
        {
            _progress.Report(task.WorkflowRunId, "error", "Task thất bại.", ex.Message);
            // Mark Failed in a FRESH scope: the run's DbContext may already be faulted (e.g. the
            // exception was a DbUpdateException), so reusing it to save the failure could itself throw
            // and leave the task stuck non-terminal.
            await MarkTaskFailedAsync(task.Id, ex.Message);
        }
    }

    // Cổng duyệt tuyến tính: bước chạy xong thì DỪNG chờ người duyệt thay vì tự enqueue bước kế.
    // Bước kế chỉ được tạo khi user bấm duyệt (ApproveStageUseCase). CurrentStage giữ nguyên ở bước
    // vừa xong để biết đang chờ duyệt cái gì. Thông báo được Add vào cùng DbContext (không SaveChanges);
    // lần SaveChanges của người gọi lưu chúng atomic với lần chuyển trạng thái.
    private async Task AdvanceLinearPipelineAsync(INotificationService notifier, AgentTask task, CancellationToken cancellationToken)
    {
        var next = DeliveryPipeline.Next(task.WorkflowRun.CurrentStage);
        if (next is null)
        {
            _progress.Report(task.WorkflowRunId, "completed", "Workflow hoàn tất — tất cả các bước đã xong.");
            CompleteRun(task.WorkflowRun);
            await notifier.NotifyRunCompletedAsync(task.WorkflowRun, cancellationToken);
        }
        else
        {
            _progress.Report(task.WorkflowRunId, "completed", $"Bước \"{task.Title}\" xong — chờ bạn duyệt để sang: {next.Title}.");
            task.WorkflowRun.Status = WorkflowRunStatus.WaitingForHuman;
            await notifier.NotifyGateOpenedAsync(task.WorkflowRun, next.Title, cancellationToken);
        }
    }

    // Chu trình tự sửa lỗi quanh Testing — KHÔNG có cổng duyệt (đây là vòng tự động, set run về Queued
    // để worker tự nhặt bước kế). Trả về true nếu đã xử lý hand-off cho chu trình này; false để
    // AdvanceLinearPipeline lo (task không thuộc chu trình).
    //   • Testing FAIL còn ngạch  → giao Developer (BugFix).
    //   • Testing FAIL hết ngạch  → kết thúc run, báo còn lỗi để người xem lại.
    //   • Testing PASS/không rõ    → trả false (pipeline tuyến tính kết thúc run như cũ).
    //   • BugFix xong              → luôn chạy lại Testing để xác minh.
    private async Task<bool> TryAdvanceTestFixCycleAsync(AppDbContext db, AgentTask task, INotificationService notifier, CancellationToken cancellationToken)
    {
        if (task.Type == AgentTaskType.Testing)
        {
            if (TestVerdictParser.Parse(task.Output) != TestVerdict.Fail)
                return false;

            var fixAttempts = await db.AgentTasks.CountAsync(
                t => t.WorkflowRunId == task.WorkflowRunId && t.Type == AgentTaskType.BugFix,
                cancellationToken);

            if (fixAttempts >= DeliveryPipeline.MaxBugFixAttempts)
            {
                _progress.Report(task.WorkflowRunId, "completed",
                    $"Vẫn còn lỗi sau {DeliveryPipeline.MaxBugFixAttempts} lần tự sửa — dừng vòng lặp, cần người xem lại báo cáo test.");
                CompleteRun(task.WorkflowRun);
                await notifier.NotifyRunCompletedAsync(task.WorkflowRun, cancellationToken);
                return true;
            }

            _progress.Report(task.WorkflowRunId, "completed",
                $"Test phát hiện lỗi — tự động giao Developer sửa (lần {fixAttempts + 1}/{DeliveryPipeline.MaxBugFixAttempts}).");
            return await EnqueueFollowUpAsync(db, task, DeliveryPipeline.BugFixStep, task.Output ?? string.Empty, cancellationToken);
        }

        if (task.Type == AgentTaskType.BugFix)
        {
            _progress.Report(task.WorkflowRunId, "completed", "Đã sửa lỗi — chạy lại Test để xác minh.");
            return await EnqueueFollowUpAsync(db, task, DeliveryPipeline.TestingStep, task.Output ?? string.Empty, cancellationToken);
        }

        return false;
    }

    // Enqueue task cho bước kế trong chu trình và đẩy run về Queued (worker tự nhặt — không cổng duyệt).
    // Thiếu agent cho vai cần thiết thì đánh Failed có thông báo rõ. Luôn trả true: đã xử lý hand-off.
    private async Task<bool> EnqueueFollowUpAsync(AppDbContext db, AgentTask previous, PipelineStep step, string input, CancellationToken cancellationToken)
    {
        var agentId = await db.Agents
            .Where(a => a.RoleKey == step.Role)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (agentId is null)
        {
            _progress.Report(previous.WorkflowRunId, "error", $"Không tìm thấy agent vai {step.Role} cho bước \"{step.Title}\".");
            FailRun(previous.WorkflowRun);
            return true;
        }

        db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = previous.WorkflowRunId,
            ProjectId = previous.ProjectId,
            AgentId = agentId,
            Type = step.TaskType,
            Status = AgentTaskStatus.Queued,
            Title = step.Title,
            Input = input
        });

        previous.WorkflowRun.CurrentStage = step.Stage;
        previous.WorkflowRun.Status = WorkflowRunStatus.Queued;
        return true;
    }

    private static void CompleteRun(WorkflowRun run)
    {
        run.Status = WorkflowRunStatus.Completed;
        run.CurrentStage = WorkflowStageKey.Completed;
        run.FinishedAt = DateTime.UtcNow;
    }

    private static void FailRun(WorkflowRun run)
    {
        run.Status = WorkflowRunStatus.Failed;
        run.CurrentStage = WorkflowStageKey.Failed;
        run.FinishedAt = DateTime.UtcNow;
    }

    // Loads the task in its own scope/DbContext and marks it (and its run) Failed, independent of any
    // faulted context from the failed run. Best-effort: a failure here is logged, not rethrown.
    private async Task MarkTaskFailedAsync(Guid taskId, string error)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notifier = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var task = await db.AgentTasks.Include(x => x.WorkflowRun).FirstOrDefaultAsync(x => x.Id == taskId);
            if (task == null)
                return;

            task.Status = AgentTaskStatus.Failed;
            task.Error = error;
            task.FinishedAt = DateTime.UtcNow;
            FailRun(task.WorkflowRun);
            await notifier.NotifyRunFailedAsync(task.WorkflowRun, error, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark agent task {TaskId} as failed.", taskId);
        }
    }

    private async Task<RequirementDraftOutcome> RunRequirementDraftAsync(IServiceScope scope, AgentTask task, CancellationToken cancellationToken)
    {
        var draftService = scope.ServiceProvider.GetRequiredService<ProductBriefDraftService>();

        var outcome = await draftService.GenerateOrUpdateDraftAsync(
            task.ProjectId,
            onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail),
            onToken: token => _progress.ReportToken(task.WorkflowRunId, token),
            workflowRunId: task.WorkflowRunId,
            cancellationToken: cancellationToken);

        _progress.Report(task.WorkflowRunId, "completed",
            outcome == RequirementDraftOutcome.NeedsMoreInfo
                ? "Cần bổ sung thông tin trước khi sinh tài liệu — xem câu hỏi trong khung chat."
                : "Đã tạo/cập nhật tài liệu requirement.");

        return outcome;
    }

    private async Task<string> RunAiDesignSpecAsync(IServiceScope scope, AgentTask task, CancellationToken cancellationToken)
    {
        var docsService = scope.ServiceProvider.GetRequiredService<RequirementDocsService>();

        // task.Input mang versionName (V{n}) của requirement vừa được duyệt — phiên bản cần sinh spec.
        return await docsService.GenerateAiDesignSpecAsync(
            task.ProjectId,
            task.Input,
            onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail),
            onToken: token => _progress.ReportToken(task.WorkflowRunId, token),
            workflowRunId: task.WorkflowRunId,
            cancellationToken: cancellationToken);
    }

    private async Task RunTechnicalDocsAsync(IServiceScope scope, AgentTask task, CancellationToken cancellationToken)
    {
        var docsService = scope.ServiceProvider.GetRequiredService<RequirementDocsService>();

        // Task chỉnh sửa mang nhận xét của người duyệt — BA sẽ cập nhật bộ tài liệu hiện có theo
        // nhận xét (prompt TechnicalDocs vốn đã ở dạng "update", chỉ cần thêm khối yêu cầu sửa).
        await docsService.GenerateTechnicalDocsAsync(
            task.ProjectId,
            onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail),
            onToken: token => _progress.ReportToken(task.WorkflowRunId, token),
            workflowRunId: task.WorkflowRunId,
            revisionFeedback: task.RevisionFeedback,
            cancellationToken: cancellationToken);

        _progress.Report(task.WorkflowRunId, "completed", "Đã tạo tài liệu kỹ thuật (BRD/SRS/FSD/UserStories).");
    }

    private async Task EnsureDesignAssetsAsync(IServiceScope scope, AppDbContext db, Guid projectId)
    {
        try
        {
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId);
            if (project == null)
                return;

            var resolver = scope.ServiceProvider.GetRequiredService<WorkspacePathResolver>();
            var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
            var implDir = Path.GetDirectoryName(resolver.GetMockupPath(projectKey));
            if (string.IsNullOrWhiteSpace(implDir))
                return;

            Directory.CreateDirectory(implDir);

            // Resolve from ContentRootPath so this worker and PromptTemplateService share the same "Prompts" root (BaseDirectory = bin output diverged from project root).
            var sourceDir = Path.Combine(_environment.ContentRootPath, "Prompts", "Design");
            var templateSrc = Path.Combine(sourceDir, "poc-template.html");
            if (!File.Exists(templateSrc))
                return;

            File.Copy(templateSrc, Path.Combine(implDir, "poc-template.html"), overwrite: true);

            // Pre-seed poc-demo.html so the dev agent edits only the content region, not re-emitting the whole shell (saves tokens per run).
            // The marker region is collapsed to a SINGLE placeholder so one deterministic ReplaceInFile works, vs reproducing the ~160-line block verbatim (always failed "Old text not found").
            await SeedPocDemoAsync(templateSrc, resolver.GetMockupPath(projectKey));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy POC design assets into the workspace.");
        }
    }

    // Copies the template into poc-demo.html, replacing the POC_CONTENT region with a placeholder but keeping the markers as a stable editable region.
    private static async Task SeedPocDemoAsync(string templateSrc, string demoPath)
    {
        var template = await File.ReadAllTextAsync(templateSrc);
        var seeded = PocTemplate.SeedFromTemplate(template);

        if (seeded == null)
        {
            // Markers missing/malformed: fall back to a raw copy so we never lose the file, but still drop
            // the developer-agent instruction comment so the served demo can't leak it as page text.
            await File.WriteAllTextAsync(demoPath, PocTemplate.StripDeveloperGuide(template));
            return;
        }

        // The instruction comment heading the template is LLM guidance, not page content; keep it out of
        // the seeded demo so a later disturbance to it can't render as raw text (see StripDeveloperGuide).
        await File.WriteAllTextAsync(demoPath, PocTemplate.StripDeveloperGuide(seeded));
    }
}