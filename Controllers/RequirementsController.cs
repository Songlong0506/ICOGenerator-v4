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
    private readonly ExportReviewPackageQuery _exportReviewPackageQuery;
    private readonly IPermissionService _permissions;
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
    private readonly RetryWorkflowUseCase _retryWorkflowUseCase;
    private readonly CheckRequirementConflictsUseCase _checkRequirementConflictsUseCase;
    private readonly ResolveRequirementConflictsUseCase _resolveRequirementConflictsUseCase;
    private readonly ConfirmSourceColumnMapUseCase _confirmSourceColumnMapUseCase;
    private readonly ConfirmPermissionMatrixUseCase _confirmPermissionMatrixUseCase;
    private readonly ConfirmFlowMapUseCase _confirmFlowMapUseCase;
    private readonly ConfirmScreenScopeUseCase _confirmScreenScopeUseCase;
    private readonly ConfirmEntityMapUseCase _confirmEntityMapUseCase;
    private readonly ConfirmNotificationMapUseCase _confirmNotificationMapUseCase;
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
       ExportReviewPackageQuery exportReviewPackageQuery,
       IPermissionService permissions,
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
       RetryWorkflowUseCase retryWorkflowUseCase,
       CheckRequirementConflictsUseCase checkRequirementConflictsUseCase,
       ResolveRequirementConflictsUseCase resolveRequirementConflictsUseCase,
       ConfirmSourceColumnMapUseCase confirmSourceColumnMapUseCase,
       ConfirmPermissionMatrixUseCase confirmPermissionMatrixUseCase,
       ConfirmFlowMapUseCase confirmFlowMapUseCase,
       ConfirmScreenScopeUseCase confirmScreenScopeUseCase,
       ConfirmEntityMapUseCase confirmEntityMapUseCase,
       ConfirmNotificationMapUseCase confirmNotificationMapUseCase,
       BAChatTurnTracker chatTurnTracker,
       ILogger<RequirementsController> logger)
    {
        _getRequirementWorkspaceQuery = getRequirementWorkspaceQuery;
        _generateRequirementDraftUseCase = generateRequirementDraftUseCase;
        _chatWithBAUseCase = chatWithBAUseCase;
        _approveRequirementUseCase = approveRequirementUseCase;
        _getDocumentDownloadQuery = getDocumentDownloadQuery;
        _exportReviewPackageQuery = exportReviewPackageQuery;
        _permissions = permissions;
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
        _retryWorkflowUseCase = retryWorkflowUseCase;
        _checkRequirementConflictsUseCase = checkRequirementConflictsUseCase;
        _resolveRequirementConflictsUseCase = resolveRequirementConflictsUseCase;
        _confirmSourceColumnMapUseCase = confirmSourceColumnMapUseCase;
        _confirmPermissionMatrixUseCase = confirmPermissionMatrixUseCase;
        _confirmFlowMapUseCase = confirmFlowMapUseCase;
        _confirmScreenScopeUseCase = confirmScreenScopeUseCase;
        _confirmEntityMapUseCase = confirmEntityMapUseCase;
        _confirmNotificationMapUseCase = confirmNotificationMapUseCase;
        _chatTurnTracker = chatTurnTracker;
        _logger = logger;
    }

    // Mọi action của controller này đều thao tác trong phạm vi MỘT project/tài liệu nên đều mang
    // [RequireProjectAccess] — chặn truy cập chéo (user thường chỉ được đụng project mình tạo; xem
    // IProjectAccessGuard). Trả về giống hệt trường hợp "không tồn tại" để không xác nhận sự tồn tại
    // của project với người ngoài.

    [RequireProjectAccess(Denial = ProjectAccessDenial.RedirectToProjects)]
    public async Task<IActionResult> Index(Guid projectId, string? version = null)
    {
        var result = await _getRequirementWorkspaceQuery.ExecuteAsync(projectId, version);
        if (result == null)
            return RedirectToAction("Index", "Projects");

        ViewBag.SelectedVersion = result.SelectedVersion;
        ViewBag.BaSupportsVision = result.BaModelSupportsVision;
        ViewBag.Coverage = result.Coverage;
        ViewBag.SpecAssumptions = result.SpecAssumptions;
        ViewBag.SpecVersion = result.SpecVersion;
        return View(result.Project);
    }

    // Chat BA dạng STREAMING — đường ghi DUY NHẤT của khung chat: một request POST xử lý trọn lượt chat
    // và trả Server-Sent Events — trạng thái ("BA đang soạn…"), token "đang gõ" (đã lọc cú pháp JSON),
    // và frame done mang bản chốt (reply + suggestions + cờ mời Write Requirement) để client render tại
    // chỗ, không reload trang.
    // Dùng fetch + đọc ReadableStream phía client (EventSource không POST được); antiforgery đi theo
    // FormData như postback thường nên AutoValidateAntiforgeryToken toàn cục vẫn phủ.
    // KHÔNG có đường postback song song: lượt chat chạy với CancellationToken.None nên một cú POST thứ
    // hai cho cùng câu hỏi (client bỏ cuộc vì proxy đệm response) sẽ nhân đôi lượt user + lời gọi LLM.
    // Client hỏng stream thì reload — nháp "đã gửi" và ChatReplyStatus lo phần phục hồi.
    // retry=true: "thử lại" lượt BA vừa lỗi LLM — xóa lượt lỗi cuối rồi chạy lại trên transcript hiện
    // có (message bị bỏ qua, KHÔNG ghi thêm lượt user). Cùng một đường SSE để mọi frame (status/token/
    // done) hành xử y hệt một lượt chat thường.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    // Chặn trước khi mở stream: client thấy !response.ok, reload rồi rơi vào đúng cổng duyệt của Index.
    [RequireProjectAccess]
    public async Task ChatStream(Guid projectId, string message, bool retry = false, bool edit = false)
    {
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
                    // edit: SỬA lượt user vừa gửi (ghi đè nội dung + xóa câu trả lời cũ) rồi trả lời lại,
                    // thay vì thêm một lượt mới — xem BAChatService.EditLastUserTurnAsync.
                    var result = retry
                        ? await _chatWithBAUseCase.RetryAsync(projectId, onStatus, onToken, CancellationToken.None)
                        : edit
                            ? await _chatWithBAUseCase.EditLastAsync(projectId, message, onStatus, onToken, CancellationToken.None)
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
                            error = edit
                                ? "Không sửa được lượt vừa gửi — tải lại trang để xem hội thoại mới nhất nhé."
                                : "Không còn lượt lỗi nào để thử lại — tải lại trang để xem hội thoại mới nhất nhé."
                        },
                        _ => (object)new
                        {
                            type = "done",
                            ok = true,
                            reply = result.Reply,
                            suggestions = result.Suggestions,
                            invitesWriteRequirement = result.InvitesWriteRequirement,
                            suggestionsMultiSelect = result.SuggestionsMultiSelect,
                            // Lượt hỏi MỘT câu MỞ (xin lời kể): không có chip, client đổi gợi ý ở ô nhập
                            // thành lời mời kể tự do.
                            openEnded = result.OpenEnded,
                            // Lượt hỏi GỘP: rỗng ở lượt hỏi một câu. Client dựng thẻ hỏi từ đây, cùng
                            // markup với bản server render lúc tải trang.
                            questions = result.Questions.Select(q => new
                            {
                                group = q.Group,
                                question = q.Question,
                                suggestions = q.Suggestions,
                                multiSelect = q.MultiSelect,
                                openEnded = q.OpenEnded
                            }),
                            coverage = result.Coverage,
                            // Cổng readiness xét trên bản đồ, KHÔNG phụ thuộc lượt này có mời hay không.
                            // Client chỉ dùng khi bản draft đã tồn tại — xem BAChatTurnResult.CoverageReady.
                            coverageReady = result.CoverageReady,
                            // Bản đồ bao phủ không gộp được lượt này (đã thử lại) ⇒ panel đang hiện bản
                            // cũ và BA cũng vừa dẫn lượt bằng bản cũ đó. Client cảnh báo ngay trên panel.
                            coverageStale = result.CoverageStale,
                            flowDiagram = result.FlowDiagram,
                            // Bảng phân quyền: chỉ có ở lượt chốt nhóm phân quyền, rỗng ở mọi lượt khác.
                            // Client dựng bảng từ đây, cùng markup với bản server render lúc tải trang.
                            permissionMatrix = result.PermissionMatrix.Select(r => new
                            {
                                screen = r.Screen,
                                function = r.Function,
                                condition = r.Condition,
                                grants = r.Grants.Select(g => new
                                {
                                    role = g.Role,
                                    scope = g.Scope,
                                    locked = g.Locked,
                                    evidence = g.Evidence
                                })
                            }),
                            // BA BẢNG CHỐT còn lại — cùng luật với permissionMatrix: chỉ có ở đúng lượt
                            // cổng của nó mở, rỗng ở mọi lượt khác, và markup client dựng phải khớp bản
                            // server render lúc tải trang (lệch nhau thì người dùng rà xong một bảng rồi
                            // F5 và thấy một bảng khác). Không bao giờ có hai bảng cùng lúc —
                            // InterviewTableGate chọn đúng một.
                            flowMap = result.FlowMap.Select(r => new
                            {
                                name = r.Name,
                                kind = r.Kind,
                                role = r.Role,
                                trigger = r.Trigger,
                                steps = r.Steps.Select(s => new
                                {
                                    actor = s.Actor,
                                    action = s.Action,
                                    outcome = s.Outcome,
                                    included = s.Included
                                })
                            }),
                            screenScopeMap = result.ScreenScopeMap.Select(r => new
                            {
                                screen = r.Screen,
                                purpose = r.Purpose,
                                functions = r.Functions.Select(f => new
                                {
                                    name = f.Name,
                                    flowSteps = f.FlowSteps,
                                    included = f.Included
                                }),
                                covers = r.Covers,
                                included = r.Included
                            }),
                            // Bước luồng chưa có màn hình nào phụ trách — phép kiểm tất định của mối nối
                            // luồng ⇄ màn hình, hiện thành một dòng nhắc dưới bảng màn hình.
                            uncoveredFlowSteps = result.UncoveredFlowSteps,
                            entityMap = result.EntityMap.Select(r => new
                            {
                                entity = r.Entity,
                                description = r.Description,
                                fields = r.Fields.Select(f => new
                                {
                                    name = f.Name,
                                    meaning = f.Meaning,
                                    used = f.Used,
                                    // Hai TRỤC của ô nhập (xem EntityFieldNote): kiểu nhập, và — chỉ với kiểu
                                    // chọn — danh sách lấy ở đâu, kèm ba ô của ba nhánh nguồn.
                                    required = f.Required,
                                    input = f.Input,
                                    source = f.Source,
                                    options = f.Options,
                                    sourceSystem = f.SourceSystem,
                                    rule = f.Rule
                                }),
                                states = r.States.Select(s => new
                                {
                                    state = s.State,
                                    entryCondition = s.EntryCondition
                                }),
                                included = r.Included,
                                locked = r.Locked,
                                evidence = r.Evidence
                            }),
                            // BẢNG THÔNG BÁO — bảng cuối cùng. Kèm luôn danh sách người nhận vì client
                            // phải dựng đúng bộ tùy chọn mà server sẽ đối chiếu lúc gửi lên.
                            notificationMap = result.NotificationMap.Select(r => new
                            {
                                entity = r.Entity,
                                @event = r.Event,
                                trigger = r.Trigger,
                                to = r.To,
                                cc = r.Cc,
                                needed = r.Needed,
                                locked = r.Locked,
                                evidence = r.Evidence
                            }),
                            recipientOptions = result.RecipientOptions
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

            // "Điều đã chốt" cập nhật SAU frame done: user đã đọc được câu trả lời, nên lời gọi LLM gộp
            // quyết định không cộng vào độ chờ cảm nhận. KHÔNG frame nào được đẩy về client — nhật ký
            // không còn mặt UI nào (bản tổng kết ở cổng tạo tài liệu đã gỡ), người đọc nó nay chỉ còn là
            // máy: ngữ cảnh chat của BA ở lượt sau, ngữ cảnh soát mâu thuẫn, bước soạn Product Brief và
            // bản xuất hội thoại. Cùng nhịp và cùng lý do với UpdateInterviewOutlookAsync ngay dưới.
            // Fail-open: lỗi thì giữ bản đang lưu.
            if (turnSucceeded)
            {
                try
                {
                    await _chatWithBAUseCase.UpdateDecisionsAsync(projectId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không cập nhật được 'Điều đã chốt' sau lượt chat của project {ProjectId}", projectId);
                }

                // Cùng nhịp hậu kỳ: gộp "triển vọng phỏng vấn" (điểm cần làm rõ + màn hình dự kiến + ví dụ
                // tính thử đã xác nhận). KHÔNG frame nào được đẩy về client: cả ba danh sách nay chỉ có
                // đường tiêu thụ của máy — OpenQuestions nạp vào ngữ cảnh chat của BA ở lượt sau (xem
                // BAChatService), PlannedScope nạp vào ngữ cảnh soát mâu thuẫn (xem
                // RequirementConflictService), WorkedExamples đi vào "## 13. Worked Examples" của AI
                // Design Spec rồi thành oracle chấm POC. Vẫn gộp ở đây (fail-open) để các đường đó có
                // bản mới nhất ngay sau lượt chat.
                try
                {
                    await _chatWithBAUseCase.UpdateInterviewOutlookAsync(projectId, CancellationToken.None);
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
    [RequireProjectAccess]
    public async Task<IActionResult> ChatReplyStatus(Guid projectId)
    {
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
    [RequireProjectAccess(Denial = ProjectAccessDenial.RedirectToProjects)]
    public async Task<IActionResult> UploadSource(Guid projectId, List<IFormFile> files, string? note = null)
    {
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
    // Kiểm tra theo id tài liệu nguồn (nguồn sự thật) — projectId trong form chỉ dùng để redirect.
    [RequireProjectAccess("id", ProjectResource.SourceFile, Denial = ProjectAccessDenial.RedirectToProjects)]
    public async Task<IActionResult> DeleteSource(Guid id, Guid projectId)
    {
        await _deleteProjectSourceUseCase.ExecuteAsync(id);
        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Denial = ProjectAccessDenial.RedirectToProjects)]
    public async Task<IActionResult> WriteRequirement(Guid projectId)
    {
        // coalesce: lượt bấm nút KHÔNG mang thông tin mới nào, nên nếu vòng soạn trước còn đang chạy thì
        // gộp về chính nó thay vì xếp thêm một run sinh lại cùng bản draft (xem StartRequirementDraftWorkflowAsync).
        await _generateRequirementDraftUseCase.ExecuteAsync(projectId, coalesceWithActiveRun: true);
        TempData["WorkflowStarted"] = true;
        return RedirectToAction(nameof(Index), new { projectId });
    }

    // KHÔNG còn endpoint "ReopenCoverage" (nút "chưa đúng?" cạnh mỗi nhóm của panel Tiến độ khai thác đã
    // gỡ): panel là bảng thuật ngữ NỘI BỘ của BA, người dùng nghiệp vụ không đọc được "Vòng đời & trạng
    // thái" để biết mình có bấm đúng nhóm hay không. Đính chính nay đi qua chat như mọi điều khác — lượt
    // chắt lọc bản đồ hạ đúng nhóm xuống [MỘT PHẦN] kèm ghi chú AskedQuestionHistory.ReopenNote (miễn
    // phanh chống-hỏi-lại cho nhóm đó), và cổng Write Requirement đóng theo ở lượt kế tiếp.

    // BẢNG CỘT của file bảng tính người dùng vừa gửi: họ tích cột nào ứng dụng dùng và sửa lại cách hiểu BA
    // đề xuất. Endpoint này CHỈ lưu bảng; ngay sau đó trình duyệt gửi tiếp tin nhắn mà SERVER soạn ra vào
    // khung chat, nên hội thoại vẫn chỉ có MỘT đường ghi (xem ConfirmSourceColumnMapUseCase). Tin nhắn lấy
    // từ response chứ không do JS ghép, vì chính nó là dấu hiệu để lượt chat kế tiếp biết mình là lượt BA
    // kể lại cách hiểu file — như bảng phân quyền.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> ConfirmColumnMap(Guid projectId, [FromForm] string mapJson)
    {
        var result = await _confirmSourceColumnMapUseCase.ExecuteAsync(projectId, mapJson, HttpContext.RequestAborted);
        return result.Files > 0
            ? Json(new { ok = true, files = result.Files, message = result.Message })
            : Json(new { ok = false, error = "Không lưu được bảng cột — tải lại trang rồi thử lại nhé." });
    }

    // BẢNG PHÂN QUYỀN — nhóm «Phân quyền theo nghiệp vụ» được chốt bằng bảng ở cuối buổi phỏng vấn thay vì
    // bằng câu hỏi giữa chừng (xem PermissionMatrixGate). Như ConfirmColumnMap: endpoint này CHỈ lưu, rồi
    // trình duyệt gửi tiếp tin nhắn mà server soạn ra vào khung chat, nên hội thoại vẫn chỉ có một đường ghi.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> ConfirmPermissionMatrix(
        Guid projectId, [FromForm] string matrixJson, [FromForm] string? rolesJson = null)
    {
        var result = await _confirmPermissionMatrixUseCase.ExecuteAsync(
            projectId, matrixJson, rolesJson, HttpContext.RequestAborted);
        return result.Rows > 0
            ? Json(new { ok = true, rows = result.Rows, message = result.Message })
            : Json(new
            {
                ok = false,
                error = string.IsNullOrEmpty(result.Error)
                    ? "Không lưu được bảng phân quyền — tải lại trang rồi thử lại nhé."
                    : result.Error
            });
    }

    // BA BẢNG CHỐT còn lại của buổi phỏng vấn — cùng khuôn HAI BƯỚC với ConfirmColumnMap/
    // ConfirmPermissionMatrix: endpoint CHỈ lưu, rồi trình duyệt gửi tiếp tin nhắn mà SERVER soạn vào khung
    // chat, nên hội thoại vẫn chỉ có MỘT đường ghi. Thứ tự bày các bảng do InterviewTableGate quyết định
    // (mỗi lượt đúng một bảng); endpoint không cần biết thứ tự đó.

    // BẢNG LUỒNG NGHIỆP VỤ — người dùng rà từng bước của từng luồng (chính + ngoại lệ).
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> ConfirmFlowMap(Guid projectId, [FromForm] string flowJson)
    {
        var result = await _confirmFlowMapUseCase.ExecuteAsync(projectId, flowJson, HttpContext.RequestAborted);
        return result.Rows > 0
            ? Json(new { ok = true, rows = result.Rows, message = result.Message })
            : Json(new { ok = false, error = "Không lưu được bảng luồng — tải lại trang rồi thử lại nhé." });
    }

    // BẢNG MÀN HÌNH — phạm vi màn hình của ứng dụng, và là nguồn DÒNG của bảng phân quyền ngay sau đó.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> ConfirmScreenScope(Guid projectId, [FromForm] string screensJson)
    {
        var result = await _confirmScreenScopeUseCase.ExecuteAsync(projectId, screensJson, HttpContext.RequestAborted);
        return result.Rows > 0
            ? Json(new { ok = true, rows = result.Rows, message = result.Message })
            : Json(new { ok = false, error = "Không lưu được bảng màn hình — tải lại trang rồi thử lại nhé." });
    }

    // BẢNG ĐỐI TƯỢNG NGHIỆP VỤ — thông tin cần lưu + vòng đời trạng thái. Vòng đời đó là nguồn DÒNG của
    // bảng thông báo ngay dưới.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> ConfirmEntityMap(Guid projectId, [FromForm] string entitiesJson)
    {
        var result = await _confirmEntityMapUseCase.ExecuteAsync(projectId, entitiesJson, HttpContext.RequestAborted);
        return result.Rows > 0
            ? Json(new { ok = true, rows = result.Rows, message = result.Message })
            : Json(new { ok = false, error = "Không lưu được bảng đối tượng — tải lại trang rồi thử lại nhé." });
    }

    // BẢNG THÔNG BÁO / NHẮC NHỞ — bảng CUỐI CÙNG của buổi phỏng vấn: mỗi sự kiện một dòng, người nhận
    // chính (To) và đồng gửi (CC) chọn từ danh sách người nhận của dự án. Như nhóm phân quyền, nhóm «Thông
    // báo / nhắc nhở» không được hỏi bằng câu hỏi nữa — xem NotificationMapGate.
    //
    // HAI payload trong MỘT lượt: `recipientsJson` là bảng "Danh sách người nhận" người dùng vừa sửa, và nó
    // phải đi cùng chuyến với bảng — nó vừa là thứ được lưu, vừa là bộ mà server đối chiếu hai ô To/CC.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> ConfirmNotificationMap(
        Guid projectId, [FromForm] string notificationsJson, [FromForm] string? recipientsJson)
    {
        var result = await _confirmNotificationMapUseCase.ExecuteAsync(
            projectId, notificationsJson, recipientsJson, HttpContext.RequestAborted);
        if (result.Rows > 0)
            return Json(new { ok = true, rows = result.Rows, message = result.Message });

        // Bảng vi phạm BẤT BIẾN (còn dòng tích "Cần" mà chưa chọn người nhận) ⇒ câu của use case đã gọi tên
        // đúng các sự kiện còn thiếu; in nguyên nó thay vì câu "tải lại trang" vô nghĩa ở ca này.
        return Json(new
        {
            ok = false,
            error = string.IsNullOrWhiteSpace(result.Error)
                ? "Không lưu được bảng thông báo — tải lại trang rồi thử lại nhé."
                : result.Error
        });
    }

    // CỔNG SOÁT MÂU THUẪN — bước 1: chạy ngay trước khi soạn tài liệu (nút "Write Requirement" gọi trước
    // khi submit form). Trả về các cặp điều đã chốt chọi nhau để người dùng chốt lại; danh sách rỗng ⇒
    // client submit form như bình thường. POST vì lượt này gọi LLM và ghi kết quả lên project.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> CheckConflicts(Guid projectId)
    {
        var conflicts = await _checkRequirementConflictsUseCase.ExecuteAsync(projectId, HttpContext.RequestAborted);
        return Json(new
        {
            ok = true,
            conflicts = conflicts.Select(c => new { topic = c.Topic, sideA = c.SideA, sideB = c.SideB, question = c.Question, options = c.Options })
        });
    }

    // CỔNG SOÁT MÂU THUẪN — bước 2: người dùng đã chốt từng điểm. Lựa chọn được ghi vào hội thoại nên
    // bước soạn tài liệu (đọc transcript) tự khắc dùng đúng phương án đã chốt.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> ResolveConflicts(Guid projectId, [FromForm] string resolutionsJson)
    {
        List<ConflictResolution> resolutions;
        try
        {
            resolutions = JsonSerializer.Deserialize<List<ConflictResolution>>(resolutionsJson ?? "[]",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<ConflictResolution>();
        }
        catch
        {
            return Json(new { ok = false, error = "Dữ liệu lựa chọn không hợp lệ." });
        }

        var result = await _resolveRequirementConflictsUseCase.ExecuteAsync(projectId, resolutions, HttpContext.RequestAborted);
        return result switch
        {
            ResolveConflictsResult.Ok => Json(new { ok = true }),
            ResolveConflictsResult.NoResolutions => Json(new { ok = false, error = "Chưa chọn phương án nào." }),
            ResolveConflictsResult.BaNotConfigured => Json(new { ok = false, error = "Chưa cấu hình agent BA." }),
            _ => Json(new { ok = false, error = "Không ghi nhận được lựa chọn." })
        };
    }

    // Ghi chú người dùng ghim trực tiếp lên bản xem trước Product Brief → gom thành một lượt phản hồi
    // trong hội thoại rồi chạy lại workflow soạn Brief (tái dùng đúng vòng "Write Requirement").
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> ReviseBrief(Guid projectId, [FromForm] string notesJson)
    {
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
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> ConfirmAssumptions(Guid projectId)
    {
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
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> ReviseAssumptions(Guid projectId, [FromForm] string correctionsJson)
    {
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Denial = ProjectAccessDenial.RedirectToProjects)]
    public async Task<IActionResult> Approve(Guid projectId)
    {
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
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> RetryWorkflow(Guid projectId, Guid? runId = null)
    {
        var result = await _retryWorkflowUseCase.ExecuteAsync(projectId, runId);

        return result == RetryWorkflowResult.Requeued
            ? Json(new { ok = true })
            : Json(new { ok = false, error = "Không tìm thấy bước thất bại nào để chạy lại. Hãy tải lại trang rồi thử lại." });
    }

    [HttpGet]
    [RequireProjectAccess]
    public async Task<IActionResult> WorkflowStatus(Guid projectId, Guid? runId = null, long afterSeq = 0)
    {
        return Json(await _getWorkflowStatusQuery.ExecuteAsync(projectId, runId, afterSeq));
    }

    // Server-Sent Events: đẩy realtime tiến độ + token "suy nghĩ" của agent cho một run, thay vì để
    // trình duyệt poll mỗi 1.5s. Trả về Task (ghi thẳng vào Response body) đúng giao thức text/event-stream.
    [HttpGet]
    // Chặn trước khi mở stream: EventSource nhận lỗi HTTP và client tự rơi về polling (vốn cũng chặn).
    [RequireProjectAccess]
    public async Task WorkflowStream(Guid projectId, Guid runId, long afterSeq = 0)
    {
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
    [RequireProjectAccess(Denial = ProjectAccessDenial.RedirectToProjects)]
    public async Task<IActionResult> NewChat(Guid projectId)
    {
        await _startNewChatUseCase.ExecuteAsync(projectId);
        return RedirectToAction(nameof(Index), new { projectId });
    }

    // Lịch sử revision của một tài liệu sinh ra (metadata) — cho modal "Lịch sử" ở trang Requirements
    // và Agent Dashboard (dashboard gọi chéo sang đây; TeamDev/Admin đều có RequirementsView).
    [HttpGet]
    [RequireProjectAccess("id", ProjectResource.Document, Message = "Document not found.")]
    public async Task<IActionResult> DocumentRevisions(Guid id)
    {
        var result = await _getDocumentRevisionsQuery.ExecuteAsync(id, HttpContext.RequestAborted);
        if (result == null)
            return NotFound("Document not found.");

        return Json(result);
    }

    // Diff một revision so với revision liền trước của cùng tài liệu (tính lúc xem).
    [HttpGet]
    [RequireProjectAccess("id", ProjectResource.DocumentRevision, Message = "Revision not found.")]
    public async Task<IActionResult> DocumentRevisionDiff(Guid id)
    {
        var result = await _getDocumentRevisionDiffQuery.ExecuteAsync(id, HttpContext.RequestAborted);
        if (result == null)
            return NotFound("Revision not found.");

        return Json(result);
    }

    [HttpGet]
    [RequireProjectAccess("id", ProjectResource.Document, Message = "Document not found.")]
    public async Task<IActionResult> DocumentPreview(Guid id)
    {
        var result = await _getDocumentPreviewQuery.ExecuteAsync(id);
        if (result == null)
            return NotFound("Document not found.");

        return Json(result);
    }

    [HttpGet]
    [RequireProjectAccess("id", ProjectResource.Document, Message = "Document not found.")]
    public async Task<IActionResult> DownloadDocument(Guid id)
    {
        var result = await _getDocumentDownloadQuery.ExecuteAsync(id);
        if (result == null)
            return NotFound("Document not found.");

        return PhysicalFile(result.FilePath, result.ContentType, result.FileName);
    }

    // Tải CẢ CHUỖI DẪN XUẤT (hội thoại BA → Product Brief → AI Design Spec → POC demo) thành một .zip để
    // đem sang một công cụ AI khác nhờ soi các mối nối giữa bốn tầng. Chỉ ĐỌC (quyền xem là đủ, như
    // DownloadDocument).
    //
    // Gói CO LẠI theo quyền của người tải, không mở rộng theo quyền của endpoint: trang Requirements cố ý
    // không hiển thị bản kỹ thuật (AI Design Spec thuộc Agent Dashboard) và bản demo (thuộc Projects), nên
    // một nút tải về ở đây không được phép âm thầm biến RequirementsView thành quyền đọc cả hai thứ đó.
    // Phần bị bỏ ra luôn được nói rõ trong 00-README.md của gói.
    [HttpGet]
    [RequireProjectAccess(Denial = ProjectAccessDenial.RedirectToProjects)]
    public async Task<IActionResult> DownloadReviewPackage(Guid projectId, string? version = null)
    {
        var access = new ReviewPackageAccess(
            CanReadDesignSpec: await _permissions.HasPermissionAsync(User, AppPermission.AgentsView, HttpContext.RequestAborted),
            CanReadPoc: await _permissions.HasPermissionAsync(User, AppPermission.ProjectsView, HttpContext.RequestAborted));

        var result = await _exportReviewPackageQuery.ExecuteAsync(
            projectId, version ?? "draft", access, HttpContext.RequestAborted);

        if (result == null)
            return RedirectToAction("Index", "Projects");

        return File(result.Content, "application/zip", result.FileName);
    }

    // Nội dung một tài liệu nguồn (ProjectSourceFile) — bubble hội thoại dùng làm src cho ảnh đính kèm.
    // Trả inline (không ép download); 404 khi nguồn đã bị xóa để bubble ẩn ảnh hỏng.
    [HttpGet]
    [RequireProjectAccess("id", ProjectResource.SourceFile, Message = "Source not found.")]
    public async Task<IActionResult> SourceContent(Guid id)
    {
        var result = await _getSourceFileContentQuery.ExecuteAsync(id, HttpContext.RequestAborted);
        if (result == null)
            return NotFound("Source not found.");

        return PhysicalFile(result.FilePath, result.ContentType);
    }
}
