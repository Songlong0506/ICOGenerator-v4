using System.Text.Json;
using System.Threading.Channels;
using ICOGenerator.Application.Agents;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Mặc định cả controller chỉ cần quyền xem; các action thay đổi dữ liệu/workflow yêu cầu RequirementsManage.
[RequirePermission(AppPermission.RequirementsView)]
public class RequirementsController : Controller
{
    private readonly GetRequirementWorkspaceQuery _getRequirementWorkspaceQuery;
    private readonly GenerateRequirementDraftUseCase _generateRequirementDraftUseCase;
    private readonly ChatWithBAUseCase _chatWithBAUseCase;
    private readonly ApproveRequirementUseCase _approveRequirementUseCase;
    private readonly GetDocumentDownloadQuery _getDocumentDownloadQuery;
    private readonly GetWorkflowStatusQuery _getWorkflowStatusQuery;
    private readonly StreamWorkflowProgressQuery _streamWorkflowProgressQuery;
    private readonly GetDocumentPreviewQuery _getDocumentPreviewQuery;
    private readonly StartNewChatUseCase _startNewChatUseCase;
    private readonly UploadProjectSourceUseCase _uploadProjectSourceUseCase;
    private readonly DeleteProjectSourceUseCase _deleteProjectSourceUseCase;
    private readonly GetDocumentRevisionsQuery _getDocumentRevisionsQuery;
    private readonly GetDocumentRevisionDiffQuery _getDocumentRevisionDiffQuery;
    private readonly GetSourceFileContentQuery _getSourceFileContentQuery;
    private readonly EstimatePocEtaQuery _estimatePocEtaQuery;
    private readonly ReviseBriefFromNotesUseCase _reviseBriefFromNotesUseCase;
    private readonly ConfirmSpecAssumptionsUseCase _confirmSpecAssumptionsUseCase;
    private readonly ReviseSpecAssumptionsUseCase _reviseSpecAssumptionsUseCase;
    private readonly UpdateWorkedExamplesUseCase _updateWorkedExamplesUseCase;
    private readonly RetryWorkflowUseCase _retryWorkflowUseCase;
    private readonly IProjectAccessGuard _projectAccess;
    private readonly BAChatTurnTracker _chatTurnTracker;
    private readonly ILogger<RequirementsController> _logger;

    // SSE frames are hand-serialized, so match the camelCase the polling JSON (and client) already use.
    private static readonly JsonSerializerOptions SseJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Nhịp gửi frame "ping" trong lúc lượt chat chạy. Phải NGẮN hơn nhiều so với ngưỡng phát hiện stream
    // treo phía client (STREAM_IDLE_TIMEOUT_MS trong requirements.js) để một nhịp lỡ không bị hiểu nhầm
    // là mất kết nối.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    public RequirementsController(
       GetRequirementWorkspaceQuery getRequirementWorkspaceQuery,
       GenerateRequirementDraftUseCase generateRequirementDraftUseCase,
       ChatWithBAUseCase chatWithBAUseCase,
       ApproveRequirementUseCase approveRequirementUseCase,
       GetDocumentDownloadQuery getDocumentDownloadQuery,
       GetWorkflowStatusQuery getWorkflowStatusQuery,
       StreamWorkflowProgressQuery streamWorkflowProgressQuery,
       GetDocumentPreviewQuery getDocumentPreviewQuery,
       StartNewChatUseCase startNewChatUseCase,
       UploadProjectSourceUseCase uploadProjectSourceUseCase,
       DeleteProjectSourceUseCase deleteProjectSourceUseCase,
       GetDocumentRevisionsQuery getDocumentRevisionsQuery,
       GetDocumentRevisionDiffQuery getDocumentRevisionDiffQuery,
       GetSourceFileContentQuery getSourceFileContentQuery,
       EstimatePocEtaQuery estimatePocEtaQuery,
       ReviseBriefFromNotesUseCase reviseBriefFromNotesUseCase,
       ConfirmSpecAssumptionsUseCase confirmSpecAssumptionsUseCase,
       ReviseSpecAssumptionsUseCase reviseSpecAssumptionsUseCase,
       UpdateWorkedExamplesUseCase updateWorkedExamplesUseCase,
       RetryWorkflowUseCase retryWorkflowUseCase,
       IProjectAccessGuard projectAccess,
       BAChatTurnTracker chatTurnTracker,
       ILogger<RequirementsController> logger)
    {
        _getRequirementWorkspaceQuery = getRequirementWorkspaceQuery;
        _generateRequirementDraftUseCase = generateRequirementDraftUseCase;
        _chatWithBAUseCase = chatWithBAUseCase;
        _approveRequirementUseCase = approveRequirementUseCase;
        _getDocumentDownloadQuery = getDocumentDownloadQuery;
        _getWorkflowStatusQuery = getWorkflowStatusQuery;
        _streamWorkflowProgressQuery = streamWorkflowProgressQuery;
        _getDocumentPreviewQuery = getDocumentPreviewQuery;
        _startNewChatUseCase = startNewChatUseCase;
        _uploadProjectSourceUseCase = uploadProjectSourceUseCase;
        _deleteProjectSourceUseCase = deleteProjectSourceUseCase;
        _getDocumentRevisionsQuery = getDocumentRevisionsQuery;
        _getDocumentRevisionDiffQuery = getDocumentRevisionDiffQuery;
        _getSourceFileContentQuery = getSourceFileContentQuery;
        _estimatePocEtaQuery = estimatePocEtaQuery;
        _reviseBriefFromNotesUseCase = reviseBriefFromNotesUseCase;
        _confirmSpecAssumptionsUseCase = confirmSpecAssumptionsUseCase;
        _reviseSpecAssumptionsUseCase = reviseSpecAssumptionsUseCase;
        _updateWorkedExamplesUseCase = updateWorkedExamplesUseCase;
        _retryWorkflowUseCase = retryWorkflowUseCase;
        _projectAccess = projectAccess;
        _chatTurnTracker = chatTurnTracker;
        _logger = logger;
    }

    // Mọi action của controller này đều thao tác trong phạm vi MỘT project/tài liệu — chặn truy cập
    // chéo (user thường chỉ được đụng project mình tạo; xem IProjectAccessGuard). Trả về giống hệt
    // trường hợp "không tồn tại" để không xác nhận sự tồn tại của project với người ngoài.
    private Task<bool> CanAccessProjectAsync(Guid projectId) =>
        _projectAccess.CanAccessProjectAsync(User, projectId, HttpContext.RequestAborted);

    public async Task<IActionResult> Index(Guid projectId, string? version = null)
    {
        if (!await CanAccessProjectAsync(projectId))
            return RedirectToAction("Index", "Projects");

        var result = await _getRequirementWorkspaceQuery.ExecuteAsync(projectId, version);
        if (result == null)
            return RedirectToAction("Index", "Projects");

        ViewBag.SelectedVersion = result.SelectedVersion;
        ViewBag.BaSupportsVision = result.BaModelSupportsVision;
        ViewBag.Coverage = result.Coverage;
        ViewBag.Decisions = result.Decisions;
        ViewBag.OpenQuestions = result.OpenQuestions;
        ViewBag.PlannedScope = result.PlannedScope;
        ViewBag.WorkedExamples = result.WorkedExamples;
        ViewBag.SpecAssumptions = result.SpecAssumptions;
        ViewBag.SpecVersion = result.SpecVersion;
        return View(result.Project);
    }

    // Đường postback cổ điển (reload cả trang) — giữ làm FALLBACK khi trình duyệt không stream được
    // (fetch/ReadableStream lỗi): requirements.js chỉ submit form này khi ChatStream thất bại từ sớm.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> Chat(Guid projectId, string message)
    {
        if (!await CanAccessProjectAsync(projectId))
            return RedirectToAction("Index", "Projects");

        if (string.IsNullOrWhiteSpace(message))
            return RedirectToAction(nameof(Index), new { projectId });

        try
        {
            var result = await _chatWithBAUseCase.ExecuteAsync(projectId, message);

            if (result.Status == ChatWithBAResult.ProjectNotFound)
                return RedirectToAction("Index", "Projects");

            if (result.Status == ChatWithBAResult.BaNotConfigured)
                TempData["Error"] = "Chưa cấu hình agent BA (RoleKey = BusinessAnalyst). Hãy tạo/kích hoạt agent BA và gán AI model trong màn hình Manage Agent.";
            else
            {
                // Đường postback reload cả trang nên các panel render từ server — gộp lượt mới trước khi
                // redirect (ở đường streaming việc này chạy sau frame done).
                await _chatWithBAUseCase.UpdateDecisionsAsync(projectId);
                await _chatWithBAUseCase.UpdateInterviewOutlookAsync(projectId);
                await _chatWithBAUseCase.EnsureProjectDomainAsync(projectId);
            }
        }
        catch (BudgetExceededException ex)
        {
            // Đã chạm trần ngân sách: đừng để văng thành lỗi 500 — báo lý do để người dùng biết vì sao BA không trả lời.
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { projectId });
    }

    // Chat BA dạng STREAMING: một request POST xử lý trọn lượt chat và trả Server-Sent Events —
    // trạng thái ("BA đang soạn…"), token "đang gõ" (đã lọc cú pháp JSON), và frame done mang bản chốt
    // (reply + suggestions + cờ mời Write Requirement) để client render tại chỗ, không reload trang.
    // Dùng fetch + đọc ReadableStream phía client (EventSource không POST được); antiforgery đi theo
    // FormData như postback thường nên AutoValidateAntiforgeryToken toàn cục vẫn phủ.
    // retry=true: "thử lại" lượt BA vừa lỗi LLM — xóa lượt lỗi cuối rồi chạy lại trên transcript hiện
    // có (message bị bỏ qua, KHÔNG ghi thêm lượt user). Cùng một đường SSE để mọi frame (status/token/
    // done/decisions/outlook) hành xử y hệt một lượt chat thường.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task ChatStream(Guid projectId, string message, bool retry = false)
    {
        // Chặn trước khi mở stream: client thấy !response.ok và tự rơi về đường postback (vốn cũng chặn).
        if (!await CanAccessProjectAsync(projectId))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.StatusCode = 200;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        // Callback token/status đến từ vòng stream LLM (đồng bộ) nên không ghi thẳng response được:
        // đẩy qua channel không giới hạn (TryWrite không block), vòng dưới đọc ra và ghi SSE frame.
        var channel = Channel.CreateUnbounded<object>();

        // Nhịp tim: một frame "ping" đều đặn trong lúc BA làm việc. Giữa "BA đang soạn câu trả lời…" và
        // frame done có thể là cả một lời gọi LLM dài mà KHÔNG có token nào (đường structured output không
        // stream) — không có nhịp tim thì client không phân biệt được "đang chờ" với "kết nối đã chết",
        // và proxy/ load balancer cũng hay tự cắt kết nối im lặng lâu. Client dựa vào nhịp này để phát
        // hiện stream treo (xem STREAM_IDLE_TIMEOUT_MS trong requirements.js) thay vì quay spinner mãi.
        using var heartbeatCts = new CancellationTokenSource();

        // Chạy lượt chat với CancellationToken.None: người dùng đóng tab giữa chừng thì turn vẫn chạy
        // trọn và lưu DB (lượt user đã lưu trước khi gọi LLM — bỏ ngang sẽ để hội thoại "cụt" không có
        // trả lời). Việc GHI response mới theo RequestAborted.
        var chatTask = RunChatAsync();
        var heartbeatTask = SendHeartbeatAsync(heartbeatCts.Token);

        var aborted = HttpContext.RequestAborted;
        var clientGone = false;

        await foreach (var ev in channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            if (clientGone)
                continue; // vẫn drain channel cho chatTask chạy nốt, chỉ thôi ghi response

            try
            {
                await Response.WriteAsync($"data: {JsonSerializer.Serialize(ev, SseJsonOptions)}\n\n", aborted);
                await Response.Body.FlushAsync(aborted);
            }
            catch (OperationCanceledException)
            {
                clientGone = true;
            }
        }

        await chatTask; // mọi lỗi đã được gói thành frame done bên trong — await chỉ để không bỏ rơi task
        await heartbeatTask; // đã bị hủy ở cuối RunChatAsync — await để không bỏ rơi task

        if (!clientGone)
        {
            try
            {
                await Response.WriteAsync("event: end\ndata: {}\n\n", aborted);
                await Response.Body.FlushAsync(aborted);
            }
            catch (OperationCanceledException) { }
        }

        async Task SendHeartbeatAsync(CancellationToken ct)
        {
            try
            {
                using var timer = new PeriodicTimer(HeartbeatInterval);
                while (await timer.WaitForNextTickAsync(ct))
                    channel.Writer.TryWrite(new { type = "ping" });
            }
            catch (OperationCanceledException)
            {
                // Lượt chat đã xong — dừng nhịp tim là đúng.
            }
        }

        // Vỏ bọc bảo đảm hai việc LUÔN xảy ra dù lượt chat vỡ kiểu gì: dừng nhịp tim và ĐÓNG channel.
        // Không có nó, một ngoại lệ lọt ra ngoài (vd bước hậu kỳ) sẽ để vòng đọc channel ở trên treo vô
        // hạn — client ngồi nhìn "BA đang soạn câu trả lời…" mà không bao giờ có frame done.
        async Task RunChatAsync()
        {
            // Sổ theo dõi "project này đang có lượt BA chạy dở" — nguồn để ChatReplyStatus phân biệt
            // "BA đang soạn thật" với "lượt trả lời đã chết" thay vì bắt client đoán. Phải bao trọn cả
            // phần hậu kỳ để một tab khác không retry đè lên lượt đang chạy.
            // "Thử lại" giành chỗ ĐỘC QUYỀN: nó chạy lại một lượt đã có trong hội thoại, nên nếu lượt đó
            // thật sự đang được trả lời ở nơi khác thì phải nhường, không thì thành hai câu trả lời cho
            // cùng một câu hỏi. Kiểm tra phải nằm ở ĐÂY (trước khi tự ghi dấu) — bên trong lượt chạy thì
            // không còn phân biệt được dấu của người khác với dấu của chính mình.
            IDisposable? exclusiveTurn = null;
            if (retry && !_chatTurnTracker.TryBeginExclusive(projectId, out exclusiveTurn))
            {
                channel.Writer.TryWrite(new
                {
                    type = "done",
                    ok = false,
                    error = "BA đang trả lời lượt này rồi — tải lại trang để xem câu trả lời nhé."
                });
                heartbeatCts.Cancel();
                channel.Writer.TryComplete();
                return;
            }
            using var turnRegistration = exclusiveTurn ?? _chatTurnTracker.Begin(projectId);

            try
            {
                await RunChatCoreAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChatStream vỡ ngoài vòng xử lý lượt cho project {ProjectId}", projectId);
            }
            finally
            {
                heartbeatCts.Cancel();
                channel.Writer.TryComplete();
            }
        }

        async Task RunChatCoreAsync()
        {
            object done;
            var turnSucceeded = false;
            try
            {
                if (!retry && string.IsNullOrWhiteSpace(message))
                {
                    done = new { type = "done", ok = false, error = "Tin nhắn trống." };
                }
                else
                {
                    Action<string> onStatus = status => channel.Writer.TryWrite(new { type = "status", text = status });
                    Action<string> onToken = token => channel.Writer.TryWrite(new { type = "token", text = token });
                    var result = retry
                        ? await _chatWithBAUseCase.RetryAsync(projectId, onStatus, onToken, CancellationToken.None)
                        : await _chatWithBAUseCase.ExecuteAsync(projectId, message, onStatus, onToken, CancellationToken.None);
                    turnSucceeded = result.Status == ChatWithBAResult.Ok;

                    done = result.Status switch
                    {
                        ChatWithBAResult.ProjectNotFound => new { type = "done", ok = false, error = "Project không tồn tại." },
                        ChatWithBAResult.BaNotConfigured => new
                        {
                            type = "done",
                            ok = false,
                            error = "Chưa cấu hình agent BA (RoleKey = BusinessAnalyst). Hãy tạo/kích hoạt agent BA và gán AI model trong màn hình Manage Agent."
                        },
                        ChatWithBAResult.NothingToRetry => new
                        {
                            type = "done",
                            ok = false,
                            error = "Không còn lượt lỗi nào để thử lại — tải lại trang để xem hội thoại mới nhất nhé."
                        },
                        _ => (object)new
                        {
                            type = "done",
                            ok = true,
                            reply = result.Reply,
                            suggestions = result.Suggestions,
                            invitesWriteRequirement = result.InvitesWriteRequirement,
                            suggestionsMultiSelect = result.SuggestionsMultiSelect,
                            coverage = result.Coverage,
                            decisions = result.Decisions,
                            flowDiagram = result.FlowDiagram
                        }
                    };
                }
            }
            catch (BudgetExceededException ex)
            {
                done = new { type = "done", ok = false, error = ex.Message };
            }
            catch (Exception ex)
            {
                // Lỗi bất ngờ: response SSE đã bắt đầu nên không còn trang lỗi nào để trả — gói thành
                // frame done (thông điệp chung) cho client hiển thị, chi tiết ghi log. KHÔNG rethrow:
                // ném tiếp chỉ làm Kestrel abort connection sau khi client đã nhận frame lỗi.
                _logger.LogError(ex, "ChatStream thất bại cho project {ProjectId}", projectId);
                done = new { type = "done", ok = false, error = "Có lỗi khi xử lý lượt chat. Vui lòng thử lại." };
            }

            channel.Writer.TryWrite(done);

            // "Điều đã chốt" cập nhật SAU frame done: user đã đọc được câu trả lời, lời gọi LLM gộp
            // quyết định không còn cộng vào độ chờ cảm nhận — panel tự thay bằng frame phụ này.
            // Fail-open: lỗi thì giữ panel bản cũ (done đã mang bản đang lưu).
            if (turnSucceeded)
            {
                try
                {
                    var decisions = await _chatWithBAUseCase.UpdateDecisionsAsync(projectId, CancellationToken.None);
                    channel.Writer.TryWrite(new { type = "decisions", decisions });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không cập nhật được 'Điều đã chốt' sau lượt chat của project {ProjectId}", projectId);
                }

                // Cùng nhịp hậu kỳ: gộp "triển vọng phỏng vấn" (điểm cần làm rõ + màn hình dự kiến + ví dụ
                // tính thử đã xác nhận) rồi đẩy frame phụ cập nhật các panel bên phải. Fail-open: lỗi thì
                // giữ panel bản cũ.
                try
                {
                    var outlook = await _chatWithBAUseCase.UpdateInterviewOutlookAsync(projectId, CancellationToken.None);
                    channel.Writer.TryWrite(new
                    {
                        type = "outlook",
                        openQuestions = outlook.OpenQuestions,
                        plannedScope = outlook.PlannedScope,
                        workedExamples = outlook.WorkedExamples
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không cập nhật được 'triển vọng phỏng vấn' sau lượt chat của project {ProjectId}", projectId);
                }

                // Phân loại miền nghiệp vụ (một lần cho mỗi dự án, fail-open bên trong) — cũng ở hậu kỳ
                // để lượt chat không phải chờ; miền chọn bucket checklist học được cho các lượt sau.
                await _chatWithBAUseCase.EnsureProjectDomainAsync(projectId, CancellationToken.None);
            }
        }
    }

    // Sau khi tải lại trang (F5) GIỮA lúc BA đang trả lời: lượt user đã lưu nhưng lượt assistant còn đang
    // sinh nền (ChatStream chạy với CancellationToken.None nên vẫn hoàn tất & lưu dù client đã rời đi).
    // Endpoint nhẹ này cho client biết câu trả lời còn "đang chờ" (lượt hội thoại mới nhất là của user)
    // để hiện lại khung "BA đang soạn…" và tự tải lại khi câu trả lời đã được lưu — tránh để bong bóng
    // trả lời "biến mất" sau F5. Kèm cờ "stale" khi lượt chờ đó đã chết hẳn (không còn tiến trình nào
    // chạy nó) để client dừng chờ và mời "Thử lại" thay vì treo mãi ở spinner. Chỉ đọc, không ghi.
    [HttpGet]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> ChatReplyStatus(Guid projectId)
    {
        if (!await CanAccessProjectAsync(projectId))
            return NotFound();

        var state = await _chatWithBAUseCase.GetReplyStateAsync(projectId, HttpContext.RequestAborted);
        return Json(new { pending = state.Pending, stale = state.Stale });
    }

    // Upload tài liệu nguồn (ảnh/PDF) cho project. Nâng trần kích thước request để cho phép vài file ảnh/PDF
    // (mặc định Kestrel ~28MB; multipart 128MB) — đặt 60MB cho cả request lẫn multipart body.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequestSizeLimit(60_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 60_000_000)]
    public async Task<IActionResult> UploadSource(Guid projectId, List<IFormFile> files, string? note = null)
    {
        if (!await CanAccessProjectAsync(projectId))
            return RedirectToAction("Index", "Projects");

        try
        {
            var result = await _uploadProjectSourceUseCase.ExecuteAsync(projectId, files, User.Identity?.Name);

            if (result.Status == UploadProjectSourceStatus.ProjectNotFound)
                return RedirectToAction("Index", "Projects");
            if (result.Status == UploadProjectSourceStatus.NoFiles)
            {
                TempData["Error"] = "Chưa chọn file nào để upload.";
            }
            else
            {
                TempData["SourceUploaded"] = true;
                // Cảnh báo rõ khi PDF là bản scan (không bóc được text ⇒ BA không đọc được nội dung),
                // tránh cảm giác "đã tải lên mà BA không thấy gì".
                if (result.ScannedPdfNames.Count > 0)
                    TempData["SourceScanWarning"] =
                        $"Tôi không đọc được nội dung bên trong các file sau: {string.Join(", ", result.ScannedPdfNames)}. "
                        + "Hãy tải lên bản có chữ (hoặc chụp ảnh trực tiếp từng trang) nếu muốn tôi đọc nội dung đó.";

                // BA đọc các nguồn mới, tóm tắt và xin xác nhận (thêm một lượt assistant) — đóng vòng phản
                // hồi ngay tại đầu vào. Kèm theo ghi chú người dùng gõ cạnh ảnh (nếu có) để BA đọc ảnh đúng
                // trọng tâm, và danh sách file vừa upload để lượt user hiển thị ảnh ngay trong hội thoại.
                // Fail-open: bước tóm tắt lỗi thì upload vẫn thành công (lỗi hiện thành lượt ⚠️ trong chat).
                var attachments = result.IngestedFiles
                    .Select(f => new ChatAttachment(f.Id, f.FileName,
                        f.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                await _chatWithBAUseCase.AcknowledgeSourcesAsync(projectId, note, attachments);
            }
        }
        catch (SourceFileValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> DeleteSource(Guid id, Guid projectId)
    {
        // Kiểm tra theo id tài liệu nguồn (nguồn sự thật) — projectId trong form chỉ dùng để redirect.
        if (!await _projectAccess.CanAccessSourceFileAsync(User, id, HttpContext.RequestAborted))
            return RedirectToAction("Index", "Projects");

        await _deleteProjectSourceUseCase.ExecuteAsync(id);
        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> WriteRequirement(Guid projectId)
    {
        if (!await CanAccessProjectAsync(projectId))
            return RedirectToAction("Index", "Projects");

        await _generateRequirementDraftUseCase.ExecuteAsync(projectId);
        TempData["WorkflowStarted"] = true;
        return RedirectToAction(nameof(Index), new { projectId });
    }

    // Ghi chú người dùng ghim trực tiếp lên bản xem trước Product Brief → gom thành một lượt phản hồi
    // trong hội thoại rồi chạy lại workflow soạn Brief (tái dùng đúng vòng "Write Requirement").
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> ReviseBrief(Guid projectId, [FromForm] string notesJson)
    {
        if (!await CanAccessProjectAsync(projectId))
            return Json(new { ok = false, error = "Không có quyền truy cập dự án." });

        List<BriefNote> notes;
        try
        {
            notes = JsonSerializer.Deserialize<List<BriefNote>>(notesJson ?? "[]",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<BriefNote>();
        }
        catch
        {
            return Json(new { ok = false, error = "Dữ liệu ghi chú không hợp lệ." });
        }

        var result = await _reviseBriefFromNotesUseCase.ExecuteAsync(projectId, notes);
        return result switch
        {
            ReviseBriefResult.Ok => Json(new { ok = true }),
            ReviseBriefResult.NoNotes => Json(new { ok = false, error = "Chưa có ghi chú nào để gửi." }),
            ReviseBriefResult.BaNotConfigured => Json(new { ok = false, error = "Chưa cấu hình agent BA." }),
            _ => Json(new { ok = false, error = "Không gửi được ghi chú." })
        };
    }

    // CỔNG XÁC NHẬN GIẢ ĐỊNH — nhánh "đồng ý": gỡ cổng rồi khởi động delivery workflow dựng POC. Trả JSON
    // (panel render bằng JS như banner workflow) thay vì redirect, để trang không nháy giữa lúc user đang
    // rà danh sách.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> ConfirmAssumptions(Guid projectId)
    {
        if (!await CanAccessProjectAsync(projectId))
            return Json(new { ok = false, error = "Không có quyền truy cập dự án." });

        var result = await _confirmSpecAssumptionsUseCase.ExecuteAsync(projectId, HttpContext.RequestAborted);
        return result switch
        {
            ConfirmAssumptionsResult.Ok => Json(new { ok = true }),
            ConfirmAssumptionsResult.NothingPending => Json(new { ok = false, error = "Không còn giả định nào đang chờ xác nhận — tải lại trang nhé." }),
            ConfirmAssumptionsResult.SpecMissing => Json(new { ok = false, error = "Không tìm thấy bản thiết kế của phiên bản này. Hãy thử duyệt lại requirement." }),
            _ => Json(new { ok = false, error = "Không xác nhận được giả định." })
        };
    }

    // CỔNG XÁC NHẬN GIẢ ĐỊNH — nhánh "có điểm chưa đúng": ghi đính chính, sinh LẠI AI Design Spec rồi
    // dựng lại cổng ở lượt sinh mới (POC chưa hề được dựng nên không có gì phải vứt đi).
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> ReviseAssumptions(Guid projectId, [FromForm] string correctionsJson)
    {
        if (!await CanAccessProjectAsync(projectId))
            return Json(new { ok = false, error = "Không có quyền truy cập dự án." });

        List<AssumptionCorrection> corrections;
        try
        {
            corrections = JsonSerializer.Deserialize<List<AssumptionCorrection>>(correctionsJson ?? "[]",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<AssumptionCorrection>();
        }
        catch
        {
            return Json(new { ok = false, error = "Dữ liệu đính chính không hợp lệ." });
        }

        var result = await _reviseSpecAssumptionsUseCase.ExecuteAsync(projectId, corrections, HttpContext.RequestAborted);
        return result switch
        {
            ReviseAssumptionsResult.Ok => Json(new { ok = true }),
            ReviseAssumptionsResult.NoNotes => Json(new { ok = false, error = "Chưa đánh dấu giả định nào chưa đúng." }),
            ReviseAssumptionsResult.NothingPending => Json(new { ok = false, error = "Không còn giả định nào đang chờ xác nhận — tải lại trang nhé." }),
            ReviseAssumptionsResult.BaNotConfigured => Json(new { ok = false, error = "Chưa cấu hình agent BA." }),
            _ => Json(new { ok = false, error = "Không gửi được đính chính." })
        };
    }

    // Sửa TAY panel "Ví dụ đã xác nhận" — oracle mà POC bị chấm theo, nên user phải chỉnh được trực tiếp
    // thay vì nói lại trong chat rồi chờ lượt chắt lọc hiểu đúng ý.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> UpdateWorkedExamples(Guid projectId, [FromForm] string examplesJson)
    {
        if (!await CanAccessProjectAsync(projectId))
            return Json(new { ok = false, error = "Không có quyền truy cập dự án." });

        List<string> examples;
        try
        {
            examples = JsonSerializer.Deserialize<List<string>>(examplesJson ?? "[]") ?? new List<string>();
        }
        catch
        {
            return Json(new { ok = false, error = "Dữ liệu ví dụ không hợp lệ." });
        }

        var result = await _updateWorkedExamplesUseCase.ExecuteAsync(projectId, examples, HttpContext.RequestAborted);
        return result switch
        {
            UpdateWorkedExamplesResult.Ok => Json(new { ok = true }),
            UpdateWorkedExamplesResult.TooMany => Json(new { ok = false, error = "Quá nhiều ví dụ — giữ tối đa 30 ví dụ thôi nhé." }),
            UpdateWorkedExamplesResult.BaNotConfigured => Json(new { ok = false, error = "Chưa cấu hình agent BA." }),
            _ => Json(new { ok = false, error = "Không lưu được danh sách ví dụ." })
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> Approve(Guid projectId)
    {
        if (!await CanAccessProjectAsync(projectId))
            return RedirectToAction("Index", "Projects");

        var result = await _approveRequirementUseCase.ExecuteAsync(projectId);

        if (result == ApproveRequirementResult.ProjectNotFound)
            return RedirectToAction("Index", "Projects");

        if (result == ApproveRequirementResult.MissingProductBrief)
        {
            TempData["Error"] = "Product Brief chưa được tạo. Vui lòng bấm \"Write Requirement\" để tạo Product Brief trước khi approve.";
            return RedirectToAction(nameof(Index), new { projectId });
        }

        if (result == ApproveRequirementResult.NoDraftDocuments)
            return RedirectToAction(nameof(Index), new { projectId });

        if (result == ApproveRequirementResult.PromotionFailed)
        {
            TempData["Error"] = "Không thể chuyển tài liệu draft sang phiên bản đã duyệt (file có thể đang bị mở/khóa). Đóng file đang mở rồi thử lại.";
            return RedirectToAction(nameof(Index), new { projectId });
        }

        if (result == ApproveRequirementResult.WorkflowStartFailed)
        {
            TempData["Error"] = "Tài liệu đã được duyệt nhưng không khởi động được workflow sinh AI Design Spec / tạo POC. Vui lòng thử lại.";
            return RedirectToAction(nameof(Index), new { projectId });
        }

        TempData["WorkflowStarted"] = true;
        // Banner kỳ vọng sau Approve: user cần biết điều gì xảy ra tiếp theo và trong bao lâu, thay vì
        // nhìn spinner vô định. Cờ riêng (không dùng chung WorkflowStarted của Write Requirement) vì
        // chỉ Approve mới dẫn tới dựng POC. ETA đo từ lịch sử vận hành; null = chưa có lịch sử.
        TempData["RequirementApproved"] = true;
        var etaMinutes = await _estimatePocEtaQuery.ExecuteAsync(HttpContext.RequestAborted);
        if (etaMinutes.HasValue)
            TempData["ApprovedPocEtaMinutes"] = etaMinutes.Value;
        return RedirectToAction(nameof(Index), new { projectId });
    }

    // Cổng DUYỆT/ĐẨY bước delivery (ApproveStage/RejectStage/RequestRevision) sống ở
    // AgentDashboardController và yêu cầu quyền DeliveryAdvance: user thường dừng ở bước POC,
    // chỉ TeamDev/Admin mới đẩy tiếp các bước Architecture/code/test trên Agent Dashboard.

    // CHẠY LẠI bước đã thất bại thì khác — lỗi thường là tạm thời (LLM rớt kết nối) và điển hình rơi
    // vào chính workflow "Write Requirement" do user thường tự chạy. Vì họ KHÔNG có quyền vào Agent
    // Dashboard, ta cho retry ngay tại trang Requirements với quyền RequirementsManage (bằng đúng quyền
    // để bấm "Write Requirement"/"Approve"). Chỉ re-queue đúng task đã hỏng — không duyệt, không đẩy bước
    // kế — nên không đụng ranh giới quyền DeliveryAdvance. Trả JSON để banner (render bằng JS) tự xử lý.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> RetryWorkflow(Guid projectId, Guid? runId = null)
    {
        if (!await CanAccessProjectAsync(projectId))
            return Json(new { ok = false, error = "Không có quyền truy cập dự án." });

        var result = await _retryWorkflowUseCase.ExecuteAsync(projectId, runId);

        return result == RetryWorkflowResult.Requeued
            ? Json(new { ok = true })
            : Json(new { ok = false, error = "Không tìm thấy bước thất bại nào để chạy lại. Hãy tải lại trang rồi thử lại." });
    }

    [HttpGet]
    public async Task<IActionResult> WorkflowStatus(Guid projectId, Guid? runId = null, long afterSeq = 0)
    {
        if (!await CanAccessProjectAsync(projectId))
            return NotFound();

        return Json(await _getWorkflowStatusQuery.ExecuteAsync(projectId, runId, afterSeq));
    }

    // Server-Sent Events: đẩy realtime tiến độ + token "suy nghĩ" của agent cho một run, thay vì để
    // trình duyệt poll mỗi 1.5s. Trả về Task (ghi thẳng vào Response body) đúng giao thức text/event-stream.
    [HttpGet]
    public async Task WorkflowStream(Guid projectId, Guid runId, long afterSeq = 0)
    {
        // Chặn trước khi mở stream: EventSource nhận lỗi HTTP và client tự rơi về polling (vốn cũng chặn).
        if (!await CanAccessProjectAsync(projectId))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.StatusCode = 200;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        // Không set header "Connection" tay: nó là reserved header, set sẽ ném lỗi dưới HTTP/2 (mặc định của Kestrel khi HTTPS).
        // Tắt buffering (cả của Kestrel lẫn reverse-proxy như nginx) để mỗi frame tới ngay browser.
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var cancellationToken = HttpContext.RequestAborted;

        try
        {
            await foreach (var ev in _streamWorkflowProgressQuery.ExecuteAsync(projectId, runId, afterSeq, cancellationToken))
            {
                var frame = ev is null
                    ? ": ping\n\n"
                    : $"data: {JsonSerializer.Serialize(ev, SseJsonOptions)}\n\n";

                await Response.WriteAsync(frame, cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }

            // Báo client đóng kết nối thay vì để EventSource tự reconnect (run đã kết thúc, không còn gì để stream).
            await Response.WriteAsync("event: end\ndata: {}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client đã rời trang (RequestAborted): kết thúc êm, không phải lỗi.
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> NewChat(Guid projectId)
    {
        if (!await CanAccessProjectAsync(projectId))
            return RedirectToAction("Index", "Projects");

        await _startNewChatUseCase.ExecuteAsync(projectId);
        return RedirectToAction(nameof(Index), new { projectId });
    }

    // Lịch sử revision của một tài liệu sinh ra (metadata) — cho modal "Lịch sử" ở trang Requirements
    // và Agent Dashboard (dashboard gọi chéo sang đây; TeamDev/Admin đều có RequirementsView).
    [HttpGet]
    public async Task<IActionResult> DocumentRevisions(Guid id)
    {
        if (!await _projectAccess.CanAccessDocumentAsync(User, id, HttpContext.RequestAborted))
            return NotFound("Document not found.");

        var result = await _getDocumentRevisionsQuery.ExecuteAsync(id, HttpContext.RequestAborted);
        if (result == null)
            return NotFound("Document not found.");

        return Json(result);
    }

    // Diff một revision so với revision liền trước của cùng tài liệu (tính lúc xem).
    [HttpGet]
    public async Task<IActionResult> DocumentRevisionDiff(Guid id)
    {
        if (!await _projectAccess.CanAccessDocumentRevisionAsync(User, id, HttpContext.RequestAborted))
            return NotFound("Revision not found.");

        var result = await _getDocumentRevisionDiffQuery.ExecuteAsync(id, HttpContext.RequestAborted);
        if (result == null)
            return NotFound("Revision not found.");

        return Json(result);
    }

    [HttpGet]
    public async Task<IActionResult> DocumentPreview(Guid id)
    {
        if (!await _projectAccess.CanAccessDocumentAsync(User, id, HttpContext.RequestAborted))
            return NotFound("Document not found.");

        var result = await _getDocumentPreviewQuery.ExecuteAsync(id);
        if (result == null)
            return NotFound("Document not found.");

        return Json(result);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadDocument(Guid id)
    {
        if (!await _projectAccess.CanAccessDocumentAsync(User, id, HttpContext.RequestAborted))
            return NotFound("Document not found.");

        var result = await _getDocumentDownloadQuery.ExecuteAsync(id);
        if (result == null)
            return NotFound("Document not found.");

        return PhysicalFile(result.FilePath, result.ContentType, result.FileName);
    }

    // Nội dung một tài liệu nguồn (ProjectSourceFile) — bubble hội thoại dùng làm src cho ảnh đính kèm.
    // Trả inline (không ép download); 404 khi nguồn đã bị xóa để bubble ẩn ảnh hỏng.
    [HttpGet]
    public async Task<IActionResult> SourceContent(Guid id)
    {
        if (!await _projectAccess.CanAccessSourceFileAsync(User, id, HttpContext.RequestAborted))
            return NotFound("Source not found.");

        var result = await _getSourceFileContentQuery.ExecuteAsync(id, HttpContext.RequestAborted);
        if (result == null)
            return NotFound("Source not found.");

        return PhysicalFile(result.FilePath, result.ContentType);
    }
}
