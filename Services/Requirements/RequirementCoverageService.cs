using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// "Bản đồ bao phủ yêu cầu" của MỘT dự án — trạng thái sống của cuộc phỏng vấn. Khác các tầng bộ nhớ
/// (<see cref="ConversationMemoryService"/> nhớ ngữ cảnh, <see cref="UserMemoryService"/> nhớ người dùng,
/// <see cref="ChecklistGapMemoryService"/> rút kinh nghiệm bộ câu hỏi), service này duy trì một bảng
/// trạng thái theo 12 nhóm thông tin cố định (khớp checklist trong <c>Prompts/BusinessAnalyst/requirement-chat.v4.md</c>):
/// nhóm nào đã [RÕ], nhóm nào [MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG] — lưu trên
/// <see cref="Project.RequirementCoverageMap"/>. Bản đồ là NGUỒN CHÂN LÝ DUY NHẤT của độ sẵn sàng:
/// BA đọc nó để chọn câu hỏi kế tiếp, panel "Tiến độ khai thác" render nó, và
/// <see cref="RequirementReadinessGate"/> suy ready TẤT ĐỊNH từ nó (mọi dòng áp dụng [RÕ] ⇔ cho phép
/// "Write Requirement") — không còn lời gọi LLM nào chấm lại, nên lượt distill này chính là "giám khảo"
/// và tiêu chí thẩm định nằm trong prompt requirement-coverage.v3. Distill đọc cả text tài liệu nguồn
/// để không bắt người dùng gõ lại điều tài liệu đính kèm đã có.
/// <para>
/// Khác hai bộ nhớ kia, việc cập nhật KHÔNG gom theo lô: bản đồ phải tươi ở từng lượt mới dẫn được câu
/// hỏi kế tiếp, nên mỗi lượt chat gộp ngay các lượt mới (thường chỉ 1–2 lượt → lời gọi rất nhẹ). Vẫn
/// <b>fail-open</b>: lời gọi lỗi thì giữ bản đồ cũ + không dời con trỏ, lượt sau gộp bù — nhưng KHÔNG
/// còn câm: thử lại MỘT lần rồi báo <see cref="CoverageUpdate.DistillFailed"/> lên tận panel tiến độ.
/// Bản đồ đứng im là chuyện người dùng phải thấy: BA đọc bản đồ CŨ nên sẽ hỏi lại đúng những nhóm họ
/// vừa trả lời, và nếu không ai nói gì thì triệu chứng đó trông y như "BA không nghe mình nói".
/// </para>
/// </summary>
public class RequirementCoverageService
{
    /// <summary>
    /// Bản đồ hiện hành sau lượt gộp + cờ "lượt gộp này đã THẤT BẠI" (đã thử lại mà vẫn lỗi ⇒ bản đồ
    /// trả về là bản CŨ, chưa có các lượt mới nhất).
    /// </summary>
    public sealed record CoverageUpdate(string? Map, bool DistillFailed);

    // Chặn trên độ dài bản đồ để không tự phình vô hạn (12 dòng gọn là đủ; model trả dài hơn thì cắt).
    private const int MaxCoverageChars = 4000;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public RequirementCoverageService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>
    /// Gộp các lượt chat mới (kể từ con trỏ) vào bản đồ rồi trả về bản đồ hiện hành để caller nạp vào
    /// prompt. <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK — bản đồ + con trỏ được ghi
    /// thẳng lên nó và lưu trong này. Fail-open: lời gọi LLM lỗi thì GIỮ bản đồ cũ và KHÔNG dời con trỏ,
    /// kèm cờ <see cref="CoverageUpdate.DistillFailed"/> để caller báo cho người dùng biết bản đồ đang cũ.
    /// </summary>
    public async Task<CoverageUpdate> UpdateAndLoadAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        var harvested = project.CoverageHarvestedTurnCount;

        // Thứ tự ổn định (CreatedAt rồi Id) để con trỏ khớp đúng các lượt đã gộp, như các bộ nhớ khác.
        var delta = await _db.AgentConversations
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip(harvested)
            .ToListAsync(cancellationToken);

        if (delta.Count == 0)
        {
            // Không có lượt mới thì cũng KHÔNG bỏ qua chốt chặn bảng-đã-chốt: một bản đồ kẹt lại từ lượt
            // trước (distill hỏng, hoặc model giữ mẩu "còn thiếu" cũ) sẽ khóa cổng readiness cho tới khi
            // có lượt chat kế tiếp, mà lượt chat kế tiếp lại chính là thứ đang bị chặn.
            await RepairMapAsync(project, cancellationToken);
            return new CoverageUpdate(project.RequirementCoverageMap, false);
        }

        // Text tài liệu nguồn (nếu có) đi kèm MỌI lần distill có lượt mới: thông tin trong tài liệu có
        // giá trị như lời người dùng nói, để bản đồ không treo [CHƯA HỎI] thứ tài liệu đã trả lời.
        var sources = await _db.ProjectSourceFiles
            .AsNoTracking()
            .Where(s => s.ProjectId == project.Id)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        var updated = await DistillAsync(project.RequirementCoverageMap, delta, sources, project, ba, model, project.Id, cancellationToken);

        // THỬ LẠI MỘT LẦN. Bản đồ là la bàn của lượt hỏi kế tiếp, nên một lời gọi hỏng không chỉ làm trễ
        // panel: BA sẽ dẫn lượt sau bằng bản đồ CHƯA có câu trả lời vừa rồi và hỏi lại đúng nhóm đó. Một
        // lần thử lại rẻ hơn nhiều so với việc bắt người dùng gõ lại câu họ vừa trả lời.
        // Lưu ý phạm vi: SDK đã tự retry các lỗi TRUYỀN TẢI (5xx/429/timeout), nên lần thử lại này nhắm
        // vào phần SDK không lo — lời gọi "thành công" nhưng trả về rỗng/không dùng được. Nó chỉ chạy
        // trên đường đã hỏng nên không cộng độ trễ vào lượt bình thường.
        if (updated == null && !cancellationToken.IsCancellationRequested)
            updated = await DistillAsync(project.RequirementCoverageMap, delta, sources, project, ba, model, project.Id, cancellationToken);

        if (updated != null)
        {
            // CHỐT CHẶN CUỐI, chạy bằng code: một nhóm không được đứng [RÕ] khi "Điểm cần làm rõ còn tồn
            // đọng" vẫn giữ một mục thuộc đúng nhóm đó. Hai danh sách này do hai lời gọi LLM khác nhau
            // chắt ra và không bao giờ nhìn thấy nhau, nên chúng nói ngược nhau mà không tầng nào biết —
            // và [RÕ] là lệnh cấm BA hỏi lại, tức mục tồn đọng ấy vĩnh viễn không được lấy. Xem
            // CoveragePendingGuard cho ca thật và cho lý do guard chạy ở đường GHI chứ không ở đường đọc.
            //
            // …và danh sách tồn đọng được LỌC trước khi ghi vào bản đồ: nó chắt ở HẬU KỲ nên luôn cũ hơn
            // bản đồ đúng một lượt, và một mục người dùng vừa trả lời mà vẫn được ghi thành mẩu
            // "còn thiếu:" là đúng cái vòng lặp kín mà CoverageStaleGapGuard sinh ra để cắt — chỉ khác
            // đường vào. Dọn ở đây thì mẩu chết không quay lại ngay ở lượt sau qua ngả tồn đọng.
            var pending = CoverageStaleGapGuard.DropAnsweredItems(
                updated, InterviewOutlookService.ParseItems(project.OpenQuestions));
            var guarded = CoveragePendingGuard.Apply(updated, pending);

            project.RequirementCoverageMap = string.IsNullOrWhiteSpace(guarded) ? null : guarded;
            project.CoverageHarvestedTurnCount = harvested + delta.Count;
            await _db.SaveChangesAsync(cancellationToken);
        }
        // updated == null ⇒ gộp lỗi: fail-open, giữ bản đồ cũ + con trỏ cũ, nạp lại như dưới — nhưng có
        // cờ để caller nói thẳng với người dùng rằng tiến độ khai thác chưa cập nhật được lượt này.

        // HAI CHỐT CHẶN CÒN LẠI, cũng chạy bằng code và cố ý đứng SAU CoveragePendingGuard. Một: mẩu
        // "còn thiếu:" mà chính bản đồ đã trả lời bị XOÁ (CoverageStaleGapGuard) — distiller được đính bản
        // đồ cũ nên nó chép lại mẩu cũ, và cổng readiness lấy nguyên mẩu ấy làm câu chặn, tức một câu hỏi
        // người dùng đã trả lời rồi được phát lại tới khi họ bỏ cuộc. Hai: hai nhóm chốt bằng
        // BẢNG («Phân quyền theo nghiệp vụ», «Thông báo / nhắc nhở») phải [RÕ] ngay khi bảng của chúng nằm
        // trong DB. Bằng chứng ở đây không do LLM chắt mà là từng ô người dùng tự tay bấm, nên nó thắng cả
        // mẩu "còn thiếu" mà distiller giữ lại lẫn một điểm tồn đọng gắn nhầm vào hai nhóm này — điểm tồn
        // đọng đó là câu hỏi CHẾT: BA bị cấm hỏi lẻ hai nhóm ấy và bảng đã chốt thì không bày lại bao giờ.
        // Chạy cả trên đường fail-open vì bản đồ cũ cũng là bản đồ mà cổng readiness sắp đọc.
        await RepairMapAsync(project, cancellationToken);

        return new CoverageUpdate(project.RequirementCoverageMap, updated == null);
    }

    // Hai chốt chặn cuối cùng, áp lên bản đồ ĐANG GIỮ (kể cả bản cũ của đường fail-open — nó cũng là bản
    // đồ mà cổng readiness sắp đọc) và CHỈ lưu khi có gì đổi: guard chạy ở mọi lượt, kể cả lượt không có
    // gì mới, nên một SaveChangesAsync vô ích ở đây là một lần ghi DB mỗi lượt chat cho không.
    //
    // Thứ tự bắt buộc: XOÁ mẩu chết trước, ÉP [RÕ] theo bảng đã chốt sau. CoverageConfirmedTableGuard là
    // guard duy nhất được nâng trạng thái (bằng chứng của nó là từng ô người dùng tự tay bấm), nên nó phải
    // là tiếng nói cuối cùng trên hai dòng chốt-bằng-bảng.
    private async Task RepairMapAsync(Project project, CancellationToken cancellationToken)
    {
        var repaired = CoverageConfirmedTableGuard.Apply(
            CoverageStaleGapGuard.Apply(project.RequirementCoverageMap),
            project.PermissionMatrix, project.NotificationMap);

        if (string.Equals(repaired, project.RequirementCoverageMap, StringComparison.Ordinal))
            return;

        project.RequirementCoverageMap = string.IsNullOrWhiteSpace(repaired) ? null : repaired;
        await _db.SaveChangesAsync(cancellationToken);
    }

    // Gộp bản đồ hiện có + các lượt mới (+ text tài liệu nguồn) thành MỘT bản đồ duy nhất. Trả về null
    // khi lời gọi lỗi/rỗng để caller fail-open (giữ bản đồ cũ, không dời con trỏ).
    private async Task<string?> DistillAsync(string? existingMap, List<AgentConversation> turns, List<ProjectSourceFile> sources, Project project, Agent ba, AiModel model, Guid projectId, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingMap))
        {
            sb.AppendLine("## Bản đồ hiện có (gộp/cập nhật cùng các lượt mới bên dưới)");
            sb.AppendLine(existingMap.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("## Các lượt hội thoại mới cần gộp vào bản đồ");
        foreach (var t in turns)
        {
            // Render chung (ConversationTurnRenderer): lượt BA kèm luôn các đáp án gợi ý đã đưa ra, để
            // câu trả lời tham chiếu ("Cả hai mục tiêu trên") không trỏ vào khoảng không → mất context.
            sb.AppendLine($"- {ConversationTurnRenderer.Render(t)}");
        }
        sb.Append(BuildSourceBriefNote(sources));

        // BẢNG PHÂN QUYỀN đã chốt — nguồn bằng chứng RIÊNG của dòng «Phân quyền theo nghiệp vụ», cùng vai
        // trò với bảng cột ở dòng «Dữ liệu / danh mục chính»: người dùng đã trả lời bằng cách chọn từng ô
        // chứ không gõ vào khung chat. Thiếu khối này thì distiller không thấy câu trả lời ở đâu cả — dòng
        // phân quyền kẹt lại, cổng readiness thay lời mời "Write Requirement" bằng một câu hỏi về đúng thứ
        // người dùng vừa tự tay chọn từng ô, và mỗi vòng lặp lại không sinh ra bằng chứng mới nào.
        var confirmedMatrix = PermissionMatrixBuilder.RenderConfirmedBlock(project.PermissionMatrix);
        if (!string.IsNullOrWhiteSpace(confirmedMatrix))
        {
            sb.AppendLine();
            sb.AppendLine("## Bảng phân quyền (người dùng đã chốt bằng cách chọn từng ô — bằng chứng cho dòng «Phân quyền theo nghiệp vụ»)");
            sb.AppendLine(confirmedMatrix);
        }

        // BẢNG THÔNG BÁO đã chốt — nguồn bằng chứng RIÊNG của dòng «Thông báo / nhắc nhở», cùng LUẬT MỘT
        // CHIỀU với bảng phân quyền ngay trên: nhóm này cũng không còn được hỏi bằng câu hỏi, nên không có
        // khối này thì không có gì để distiller chấm [RÕ]. Khối tự chở phần "còn trống người nhận" nên một
        // bảng gửi đi mà bỏ dở vẫn chỉ lên [MỘT PHẦN] — xem NotificationMapBuilder.RenderConfirmedBlock.
        var confirmedNotifications = NotificationMapBuilder.RenderConfirmedBlock(project.NotificationMap);
        if (!string.IsNullOrWhiteSpace(confirmedNotifications))
        {
            sb.AppendLine();
            sb.AppendLine("## Bảng thông báo (người dùng đã chốt từng sự kiện — bằng chứng cho dòng «Thông báo / nhắc nhở»)");
            sb.AppendLine(confirmedNotifications);
        }

        // BA BẢNG CHỐT còn lại, cùng vai trò bằng chứng nhưng KHÔNG cùng luật: dòng phân quyền ở trên có
        // luật một chiều "chưa có bảng ⇒ không bao giờ [RÕ]", ba bảng này thì KHÔNG — chúng chỉ xác nhận
        // lại thứ hội thoại đã trả lời. Áp luật một chiều cho chúng là dựng một vòng khóa kín: cổng bày
        // bảng đòi nhóm [RÕ] mới mở, còn bản đồ đòi có bảng mới [RÕ]. Xem InterviewTableGate.
        AppendTableEvidence(sb, FlowMapBuilder.RenderConfirmedBlock(project.FlowMap),
            "## Bảng luồng nghiệp vụ (người dùng đã rà từng bước — bằng chứng cho «Chức năng & luồng nghiệp vụ chính» và «Luồng ngoại lệ»)");
        AppendTableEvidence(sb, ScreenScopeMapBuilder.RenderConfirmedBlock(project.ScreenScopeMap),
            "## Bảng màn hình (người dùng đã chốt phạm vi màn hình)");
        AppendTableEvidence(sb, EntityMapBuilder.RenderConfirmedBlock(project.EntityMap),
            "## Bảng đối tượng nghiệp vụ (người dùng đã rà — bằng chứng cho «Dữ liệu / danh mục chính» và «Vòng đời & trạng thái»)");
        AppendTableEvidence(sb, ReportMapBuilder.RenderConfirmedBlock(project.ReportMap),
            "## Bảng báo cáo / thống kê (người dùng đã rà từng dòng — bằng chứng cho «Báo cáo / thống kê»)");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/requirement-coverage.v3.md")),
            new(ChatRole.User, sb.ToString())
        };

        var result = await _llm.ChatWithLogAsync(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BARequirementCoverage"),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Content))
            return null;

        var map = result.Content.Trim();
        return map.Length > MaxCoverageChars ? map[..MaxCoverageChars] : map;
    }

    // Đính một khối bảng đã chốt vào phần bằng chứng của lượt distill. Chưa chốt ⇒ không đính gì.
    private static void AppendTableEvidence(StringBuilder sb, string? block, string heading)
    {
        if (string.IsNullOrWhiteSpace(block))
            return;

        sb.AppendLine();
        sb.AppendLine(heading);
        sb.AppendLine(block);
    }

    // Tóm tắt (text) tài liệu nguồn cho lượt distill — call text-only nên KHÔNG kèm ảnh được; bù lại
    // nêu tên file + trích text (bóc từ PDF) có giới hạn, để bản đồ ghi nhận được thứ tài liệu đã có.
    //
    // Kèm luôn BẢNG CỘT đã được người dùng chốt (SourceColumnMapBuilder.RenderConfirmedBlock) — đây là
    // câu trả lời của người dùng cho nhóm "Dữ liệu / danh mục chính", chỉ khác là họ trả lời bằng cách
    // tích một bảng chứ không gõ vào khung chat. Thiếu khối này thì distiller không thấy nó ở đâu cả:
    // dòng "Dữ liệu / danh mục chính" kẹt [MỘT PHẦN] với "còn thiếu: chốt bộ cột chính thức", cổng
    // readiness thay lời mời "Write Requirement" của BA bằng câu hỏi dựng sẵn (RequirementReadinessGate),
    // và người dùng bị hỏi lại đúng thứ họ vừa tự tay duyệt từng dòng — lặp mãi vì mỗi vòng lại không
    // sinh ra bằng chứng mới nào cho distiller.
    private static string BuildSourceBriefNote(List<ProjectSourceFile> sources)
    {
        if (sources.Count == 0)
            return string.Empty;

        const int maxCharsPerFile = 4000;
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"## Tài liệu nguồn (người dùng đã đính kèm {sources.Count} tài liệu: {string.Join(", ", sources.Select(s => s.FileName))})");
        foreach (var s in sources)
        {
            if (!string.IsNullOrWhiteSpace(s.ExtractedText))
            {
                var text = s.ExtractedText!.Length > maxCharsPerFile
                    ? s.ExtractedText[..maxCharsPerFile] + "…(đã cắt bớt)"
                    : s.ExtractedText;
                sb.AppendLine($"[Nội dung trích từ {s.FileName}]");
                sb.AppendLine(text);
            }

            var confirmedColumns = SourceColumnMapBuilder.RenderConfirmedBlock(s.FileName, s.ColumnMap);
            if (!string.IsNullOrWhiteSpace(confirmedColumns))
                sb.AppendLine(confirmedColumns);
        }
        return sb.ToString();
    }
}
