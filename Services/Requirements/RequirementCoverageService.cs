using System.Text;
using ICOGenerator.Contracts.Requirements;
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
/// và tiêu chí thẩm định nằm trong prompt requirement-coverage.v5. Distill đọc cả text tài liệu nguồn
/// để không bắt người dùng gõ lại điều tài liệu đính kèm đã có.
/// <para>
/// <b>Lượt distill này ghi HAI cột.</b> Bản đồ (<see cref="Project.RequirementCoverageMap"/> — trạng thái
/// 12 nhóm) và danh sách CÂU HỎI (<see cref="Project.OpenQuestions"/>) ra đời trong cùng một lời gọi, vì
/// chúng ràng buộc nhau chặt tới mức chỉ đúng khi được viết cùng nhau: một nhóm còn câu hỏi MỞ thì dòng
/// của nó không được <c>[RÕ]</c>. Khi danh sách câu hỏi còn được chắt bởi một lời gọi RIÊNG chạy ở hậu kỳ
/// (<see cref="InterviewOutlookService"/>), nó luôn cũ hơn bản đồ đúng một lượt — nên cổng "Write
/// Requirement" bày ra một câu hỏi người dùng vừa trả lời xong, và cả một tầng hoà giải phải sinh ra để
/// che độ trễ đó. Xem <see cref="CoverageDistillDocument"/>.
/// </para>
/// <para>
/// Khác hai bộ nhớ kia, việc cập nhật KHÔNG gom theo lô: bản đồ phải tươi ở từng lượt mới dẫn được câu
/// hỏi kế tiếp, nên mỗi lượt chat gộp ngay các lượt mới (thường chỉ 1–2 lượt → lời gọi rất nhẹ). Vẫn
/// <b>fail-open</b>: lời gọi lỗi thì giữ bản cũ + không dời con trỏ, lượt sau gộp bù — nhưng KHÔNG
/// còn câm: thử lại MỘT lần rồi báo <see cref="CoverageUpdate.DistillFailed"/> lên tận panel tiến độ.
/// Bản đồ đứng im là chuyện người dùng phải thấy: BA đọc bản đồ CŨ nên sẽ hỏi lại đúng những nhóm họ
/// vừa trả lời, và nếu không ai nói gì thì triệu chứng đó trông y như "BA không nghe mình nói".
/// </para>
/// </summary>
public class RequirementCoverageService
{
    /// <summary>
    /// Bản đồ + danh sách câu hỏi hiện hành sau lượt gộp, kèm cờ "lượt gộp này đã THẤT BẠI" (đã thử lại mà
    /// vẫn lỗi ⇒ hai thứ trả về là bản CŨ, chưa có các lượt mới nhất).
    /// <para>
    /// Câu hỏi đi kèm chứ không để caller tự đọc lại <c>project.OpenQuestions</c>: lượt gộp có thể chạy
    /// trong một DI scope RIÊNG (<c>BAChatService.PrepareTurnContextAsync</c>), nên entity Project mà
    /// caller đang giữ không thấy được thứ scope kia vừa ghi.
    /// </para>
    /// </summary>
    public sealed record CoverageUpdate(string? Map, IReadOnlyList<OpenQuestionEntry> Questions, bool DistillFailed);

    /// <summary>
    /// Chặn trên độ dài bản đồ để không tự phình vô hạn. Nới từ 4000 lên khi <c>known</c> thành danh
    /// sách: bản cũ ép mỗi nhóm về "tối đa ~2 câu" nên 4000 là rộng rãi, còn bản này cố ý giữ đủ chi
    /// tiết cho bước soạn Product Brief đọc lại (~660 ký tự cho mỗi nhóm trong 12 nhóm). Trần vẫn phải
    /// có: bản đồ đi vào prompt ở MỌI lượt chat.
    /// </summary>
    private const int MaxCoverageChars = 8000;

    /// <summary>
    /// Trần của MỘT mẩu <c>known</c>. Một mẩu là một ý người dùng đã nói; dài hơn thế này thì model đang
    /// nhét cả một đoạn vào một phần tử, và phần đuôi của nó là thứ đầu tiên bị cắt khi bản đồ chạm trần.
    /// </summary>
    private const int MaxKnownItemChars = 400;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly CoverageChecklist _checklist;

    public RequirementCoverageService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts, CoverageChecklist checklist)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _checklist = checklist;
    }

    /// <summary>
    /// Gộp các lượt chat mới (kể từ con trỏ) vào bản đồ + danh sách câu hỏi rồi trả về bản hiện hành để
    /// caller nạp vào prompt. <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK — hai cột + con trỏ
    /// được ghi thẳng lên nó và lưu trong này. Fail-open: lời gọi LLM lỗi thì GIỮ bản cũ và KHÔNG dời con
    /// trỏ, kèm cờ <see cref="CoverageUpdate.DistillFailed"/> để caller báo cho người dùng biết bản đồ đang cũ.
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
            // Không có lượt mới thì cũng KHÔNG bỏ qua chuỗi guard: một bản đồ kẹt lại từ lượt trước (distill
            // hỏng, hoặc model giữ một câu hỏi đã chết) sẽ khóa cổng readiness cho tới khi có lượt chat kế
            // tiếp, mà lượt chat kế tiếp lại chính là thứ đang bị chặn.
            await RepairAsync(project, cancellationToken);
            return Current(project, distillFailed: false);
        }

        // Text tài liệu nguồn (nếu có) đi kèm MỌI lần distill có lượt mới: thông tin trong tài liệu có
        // giá trị như lời người dùng nói, để bản đồ không treo [CHƯA HỎI] thứ tài liệu đã trả lời.
        var sources = await _db.ProjectSourceFiles
            .AsNoTracking()
            .Where(s => s.ProjectId == project.Id)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        var distilled = await DistillAsync(delta, sources, project, ba, model, cancellationToken);

        // THỬ LẠI MỘT LẦN. Bản đồ là la bàn của lượt hỏi kế tiếp, nên một lời gọi hỏng không chỉ làm trễ
        // panel: BA sẽ dẫn lượt sau bằng bản đồ CHƯA có câu trả lời vừa rồi và hỏi lại đúng nhóm đó. Một
        // lần thử lại rẻ hơn nhiều so với việc bắt người dùng gõ lại câu họ vừa trả lời.
        // Lưu ý phạm vi: SDK đã tự retry các lỗi TRUYỀN TẢI (5xx/429/timeout), nên lần thử lại này nhắm
        // vào phần SDK không lo — lời gọi "thành công" nhưng trả về rỗng/không dùng được. Nó chỉ chạy
        // trên đường đã hỏng nên không cộng độ trễ vào lượt bình thường.
        if (distilled == null && !cancellationToken.IsCancellationRequested)
            distilled = await DistillAsync(delta, sources, project, ba, model, cancellationToken);

        if (distilled == null)
        {
            // Gộp lỗi: fail-open — giữ bản cũ + con trỏ cũ. Chuỗi guard vẫn chạy vì bản cũ cũng là bản mà
            // cổng readiness sắp đọc; cờ đi kèm để caller nói thẳng với người dùng rằng tiến độ khai thác
            // chưa cập nhật được lượt này.
            await RepairAsync(project, cancellationToken);
            return Current(project, distillFailed: true);
        }

        var items = CoverageMapParser.ToItems(new CoverageMapDocument { Items = distilled.Items }).ToList();
        var questions = Canonicalize(InterviewOutlookParser.ToOpenQuestions(distilled.Questions), items).ToList();

        // Bản đồ CŨ đọc TRƯỚC khi ghi đè cột: CoverageKnownLossGuard cần nó để trả lại phần đã ghi nhận
        // của một dòng vừa bị xoá trắng.
        ApplyGuards(project, items, questions, CoverageMapParser.Parse(project.RequirementCoverageMap));
        Cap(items);

        project.RequirementCoverageMap = CoverageMapParser.Serialize(items);
        project.OpenQuestions = InterviewOutlookParser.SerializeOpenQuestions(questions);
        project.CoverageHarvestedTurnCount = harvested + delta.Count;
        await _db.SaveChangesAsync(cancellationToken);

        return new CoverageUpdate(project.RequirementCoverageMap, questions, DistillFailed: false);
    }

    /// <summary>Bản đồ + danh sách câu hỏi đang lưu của dự án (không gọi LLM).</summary>
    private static CoverageUpdate Current(Project project, bool distillFailed) => new(
        project.RequirementCoverageMap,
        InterviewOutlookParser.ParseOpenQuestions(project.OpenQuestions),
        distillFailed);

    // Chạy chuỗi guard trên hai cột ĐANG LƯU (kể cả bản cũ của đường fail-open — nó cũng là bản mà cổng
    // readiness sắp đọc) và CHỈ lưu khi có gì đổi: guard chạy ở mọi lượt, kể cả lượt không có gì mới, nên
    // một SaveChangesAsync vô ích ở đây là một lần ghi DB mỗi lượt chat cho không.
    private async Task RepairAsync(Project project, CancellationToken cancellationToken)
    {
        var items = CoverageMapParser.Parse(project.RequirementCoverageMap).ToList();
        var questions = InterviewOutlookParser.ParseOpenQuestions(project.OpenQuestions).ToList();
        if (items.Count == 0 && questions.Count == 0)
            return;

        // Đường sửa chữa chạy trên CHÍNH bản đang lưu, nên "bản đồ trước đó" là chính nó ⇒
        // CoverageKnownLossGuard không có gì để trả lại và im lặng, đúng như mong đợi.
        ApplyGuards(project, items, questions, items);

        var map = items.Count == 0 ? null : CoverageMapParser.Serialize(items);
        var open = InterviewOutlookParser.SerializeOpenQuestions(questions);
        if (string.Equals(map, project.RequirementCoverageMap, StringComparison.Ordinal)
            && string.Equals(open, project.OpenQuestions, StringComparison.Ordinal))
        {
            return;
        }

        project.RequirementCoverageMap = map;
        project.OpenQuestions = open;
        await _db.SaveChangesAsync(cancellationToken);
    }

    // NĂM CHỐT CHẶN TẤT ĐỊNH của đường ghi, sửa TẠI CHỖ cả bản đồ lẫn danh sách câu hỏi. Thứ tự bắt buộc,
    // và nó chỉ có một cách đọc: DỌN danh sách câu hỏi trước, ÁP bất biến sau.
    //
    //  0. TRẢ LẠI phần đã ghi nhận bị xoá trắng (CoverageKnownLossGuard) — chạy TRƯỚC HẾT vì bốn lớp
    //     dưới đều đọc `known` để quyết định: một dòng bị nuốt mất phần đã ghi nhận thì guard xoá câu hỏi
    //     đã chết không thấy câu trả lời ở đâu, và cổng readiness không có gì để phát lại.
    //  1. XOÁ câu hỏi đã chết (CoverageStaleGapGuard) — distiller được đính chính danh sách cũ nên cách rẻ
    //     nhất để nó "hợp lệ" là chép lại nguyên câu cũ, kể cả câu mà chính bản đồ vừa trả lời. Cổng
    //     readiness lấy nguyên câu ấy làm câu chặn ⇒ người dùng bị hỏi lại thứ họ vừa nói, mãi mãi.
    //  2. XOÁ câu hỏi KHÔNG HỎI ĐƯỢC GÌ (CoverageQuestionGuard) — một câu tường thuật trạng thái ("Bảng
    //     thông báo theo sự kiện chưa được chốt") lên tới màn hình thì người dùng không có cách nào trả lời.
    //     Đứng SAU (1) và TRƯỚC (3): dọn một câu rác trước thì dòng quy tắc nhận được câu hỏi xin ví dụ số;
    //     dọn sau thì nó nhường chỗ cho đúng cái rác vừa bị lọc.
    //  3. ĐÒI ví dụ số cho quy tắc định lượng (CoverageWorkedExampleGuard) — công thức hiểu sai là lỗi
    //     không cổng nào phía sau bắt được, vì mọi cổng chỉ hỏi "có thông tin chưa".
    //  4. ÉP [RÕ] theo BẢNG đã chốt (CoverageConfirmedTableGuard) — bằng chứng ở đây không do LLM chắt mà
    //     là từng ô người dùng tự tay bấm, nên nó thắng cả câu hỏi mà distiller giữ lại. Nó cũng xoá luôn
    //     câu hỏi của hai nhóm ấy: BA bị cấm hỏi lẻ chúng và bảng không bày lại bao giờ.
    //  5. HẠ [RÕ] của nhóm còn câu hỏi MỞ (CoveragePendingGuard) — bất biến trung tâm, nên nó chạy CUỐI:
    //     bốn lớp trên vừa có quyền xoá câu hỏi, hạ dòng trước chúng là hạ vì một câu sắp bị xoá.
    private void ApplyGuards(Project project, List<CoverageMapItem> items, List<OpenQuestionEntry> questions,
        IReadOnlyList<CoverageMapItem> previous)
    {
        CoverageKnownLossGuard.Apply(items, previous);
        CoverageStaleGapGuard.Apply(items, questions);
        CoverageQuestionGuard.Apply(questions);
        CoverageWorkedExampleGuard.Apply(items, questions,
            InterviewOutlookParser.ParseWorkedExamples(project.WorkedExamples));
        CoverageConfirmedTableGuard.Apply(items, questions, project.PermissionMatrix, project.NotificationMap);
        CoveragePendingGuard.Apply(items, questions);
    }

    /// <summary>
    /// Chốt nhãn nhóm của từng câu hỏi về ĐÚNG một trong 12 nhãn checklist — ngay ở ĐƯỜNG GHI, trước khi
    /// danh sách được lưu.
    /// <para>
    /// <b>Vì sao ở đây chứ không ở chỗ đối chiếu.</b> Nhãn này là đầu vào của bốn chốt chặn tất định nhưng
    /// do model điền, nên nó lệch được theo đủ kiểu: *"Luồng ngoại lệ"* cho *"Luồng ngoại lệ &amp; trường
    /// hợp đặc biệt"*, hoặc một cái tên model tự nghĩ ra. Chuẩn hoá một lần ở đường ghi thì mọi tầng đọc
    /// sau đó thấy CÙNG một nhãn và chỉ còn đọc thuộc tính. Nhãn không khớp nhóm nào ⇒ để RỖNG: guard bỏ
    /// qua mục không nhóm, tức fail-open — câu hỏi vẫn nằm trong ngữ cảnh chat để BA hỏi, chỉ không hạ
    /// được dòng bản đồ nào.
    /// </para>
    /// Đối chiếu với checklist bóc từ prompt chứ không với 12 nhãn model vừa xuất: nhãn của chính lượt này
    /// cũng do model viết, lấy nó làm chuẩn là để một lần viết chệch tự hợp thức hoá nó. Checklist rỗng
    /// (không bóc được từ prompt) ⇒ trả nguyên, giữ lại nhãn model đưa còn hơn xoá trắng đầu vào của guard.
    /// </summary>
    private IReadOnlyList<OpenQuestionEntry> Canonicalize(IReadOnlyList<OpenQuestionEntry> questions, IReadOnlyList<CoverageMapItem> items)
    {
        var labels = _checklist.Skeleton().Select(x => x.Label).Where(l => l.Length > 0).ToList();
        if (labels.Count == 0)
            return questions;

        foreach (var question in questions)
        {
            question.Group = labels.FirstOrDefault(label => CoverageMapParser.IsSameGroup(label, question.Group))
                             ?? string.Empty;
        }

        return questions;
    }

    // Gộp trạng thái hiện có + các lượt mới (+ text tài liệu nguồn) thành MỘT bản đồ và MỘT danh sách câu
    // hỏi. Trả về null khi lời gọi lỗi/rỗng để caller fail-open (giữ bản cũ, không dời con trỏ).
    private async Task<CoverageDistillDocument?> DistillAsync(List<AgentConversation> turns, List<ProjectSourceFile> sources, Project project, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        // Chuẩn hoá về JSON trước khi nạp: model được yêu cầu XUẤT JSON, nên cho nó đọc bản đồ hiện có ở
        // cùng format là bỏ đi một phép dịch mà nó phải tự làm. Đây cũng là đường nâng cấp cho dự án cũ —
        // bản đồ text trong DB được Parse rồi Serialize, nên lượt distill đầu tiên sau khi đổi format vẫn
        // thấy đủ 12 dòng chứ không mở màn bằng một bản đồ trống.
        var existingItems = CoverageMapParser.Parse(project.RequirementCoverageMap);
        if (existingItems.Count > 0)
        {
            sb.AppendLine("## Bản đồ hiện có (gộp/cập nhật cùng các lượt mới bên dưới)");
            sb.AppendLine(CoverageMapParser.Serialize(existingItems));
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
        sb.Append(BuildQuestionNote(project));

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
            new(ChatRole.System, _prompts.Get(CoverageChecklist.CoveragePromptPath)),
            new(ChatRole.User, sb.ToString())
        };

        // Structured output: cả hai danh sách là JSON, nên schema được gửi thẳng cho model thay vì dặn dò
        // bằng lời. Model/endpoint không nhận response_format ⇒ ILlmClient tự lùi về đường văn xuôi và trả
        // Value null; lúc đó LlmJson bóc lấy — đúng đường "parse tay" mà mọi service khác của repo dùng, và
        // nó lo luôn hàng rào ```json lẫn câu dẫn quanh object. Một model yếu không làm hỏng lượt, chỉ mất
        // bảo đảm cú pháp.
        var (result, value) = await _llm.ChatStructuredAsync<CoverageDistillDocument>(
            model, messages, ba.Temperature, new ModelCallLogContext(project.Id, ba, "BARequirementCoverage"),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess)
            return null;

        // Đường parse tay dùng CÙNG bộ tuỳ chọn với đường đọc bản đồ đã lưu: một model không nhận
        // response_format cũng là một model dễ trả `known` ở dạng chuỗi, và bắt cả lượt hỏng vì chuyện đó
        // thì đúng là fail-closed ở nhánh vốn sinh ra để fail-open.
        var distilled = value ?? LlmJson.TryDeserialize<CoverageDistillDocument>(
            result.Content, options: CoverageMapParser.SerializerOptions);

        // Không đọc ra dòng nào ⇒ coi như lời gọi hỏng: caller fail-open (giữ bản cũ, không dời con trỏ) và
        // thử lại một lần. Ghi đè bản đồ đang có bằng một bản rỗng là xoá trắng tiến độ khai thác. Danh
        // sách câu hỏi RỖNG thì ngược lại — đó là một câu trả lời hợp lệ ("không còn gì phải hỏi"), nên nó
        // không được tính là lỗi.
        return distilled == null || CoverageMapParser.ToItems(new CoverageMapDocument { Items = distilled.Items }).Count == 0
            ? null
            : distilled;
    }

    /// <summary>
    /// Chặn trên độ dài bản đồ, cắt theo NỘI DUNG chứ không cắt chuỗi JSON. Bản đầu tiên cắt thẳng
    /// <c>map[..MaxCoverageChars]</c> — với format text thì chỉ mất một dòng cuối, còn với JSON thì đó là
    /// một tài liệu vỡ cú pháp, tức mất TRẮNG cả bản đồ ở đúng lúc nó dài nhất. Ở đây 12 nhãn và 12 trạng
    /// thái luôn sống sót, và đó là hai thứ cổng readiness với panel tiến độ cần để không bị mù.
    ///
    /// <para>
    /// <b>Không bao giờ cắt giữa từ.</b> Bản trước chia đôi trường dài nhất cho tới khi vừa trần, nên nó
    /// đẻ ra đúng những dòng người dùng đọc thấy trên panel: <i>"…mỗi nhân viên s"</i>, <i>"…do người
    /// quản trị hệ th"</i>. Một mẩu bị cắt giữa từ thì không rà được, mà nhánh PHÁT LẠI của cổng readiness
    /// lại hỏi đúng một câu về nó ("phần này còn chỗ nào chưa đúng không?"). Hai phép cắt dưới đây đều
    /// dừng ở ranh giới TỪ, và phép thứ hai bỏ nguyên một mẩu chứ không xén nó.
    /// </para>
    ///
    /// <para>
    /// Thứ tự cũng khác bản trước, vốn hạ trường <c>evidence</c> trước tiên — tức phá đúng tính nguyên văn
    /// là toàn bộ lý do trường ấy tồn tại. Ở đây: xén các mẩu DÀI BẤT THƯỜNG trước (một mẩu đúng chuẩn là
    /// một ý, không phải một đoạn), rồi mới bỏ mẩu CŨ NHẤT của dòng đang nhiều mẩu nhất — cùng cách cân
    /// giá với trần 25 mục của checklist BA học được: mẩu cũ nhất là mẩu đã có nhiều lượt để được nói lại.
    /// </para>
    /// </summary>
    private static void Cap(IReadOnlyList<CoverageMapItem> items)
    {
        foreach (var item in items)
        {
            if (item.Known.Any(k => k.Length > MaxKnownItemChars))
                item.Known = item.Known.Select(k => ClipToWord(k, MaxKnownItemChars)).ToList();
        }

        while (CoverageMapParser.Serialize(items).Length > MaxCoverageChars)
        {
            // Dòng nhiều mẩu nhất, hoà thì dòng dài nhất — dòng chỉ còn một mẩu KHÔNG bị đụng tới ở vòng
            // này: bỏ nó là xoá trắng phần đã ghi nhận của cả một nhóm, đúng thứ CoverageKnownLossGuard
            // vừa dựng lên để chặn.
            var fattest = items
                .Where(x => x.Known.Count > 1)
                .OrderByDescending(x => x.Known.Count)
                .ThenByDescending(x => x.Known.Sum(k => k.Length))
                .FirstOrDefault();

            if (fattest == null)
                break;

            fattest.Known = fattest.Known.Skip(1).ToList();
        }
    }

    /// <summary>
    /// Cắt <paramref name="text"/> về tối đa <paramref name="max"/> ký tự, DỪNG Ở RANH GIỚI TỪ cuối cùng
    /// nằm trong trần, rồi đóng bằng "…" để người đọc biết mình đang đọc một mẩu đã bị xén. Không có ranh
    /// giới nào (một "từ" dài hơn cả trần — chỉ xảy ra với nội dung hỏng) thì cắt cứng: thà một mẩu xấu
    /// còn hơn một bản đồ không bao giờ vừa trần.
    /// </summary>
    private static string ClipToWord(string text, int max)
    {
        if (text.Length <= max)
            return text;

        var head = text[..max];
        var lastSpace = head.LastIndexOf(' ');
        return (lastSpace > 0 ? head[..lastSpace] : head).TrimEnd(' ', ',', ';', '.', '-', '—') + "…";
    }

    // DANH SÁCH CÂU HỎI HIỆN CÓ, echo lại cho chính lượt này cập nhật — cùng vai trò với khối "Bản đồ hiện
    // có" ở trên: lượt distill là một phép GỘP LŨY TIẾN, nên nó phải thấy bản cũ mới giữ được thứ các lượt
    // trước đã chắt.
    //
    // Khối này in kèm NHÃN NHÓM (ToTaggedText) — chỗ DUY NHẤT nhãn được in ra cùng câu hỏi. Model cần thấy
    // cặp nhóm↔câu hỏi để không gán lại một mục cũ sang nhóm khác; còn ngữ cảnh chat của BA thì không bao
    // giờ được thấy nhãn (xem BAChatPromptBlocks.OpenQuestions).
    //
    // Và nó in cả mục ĐÃ TRẢ LỜI. Đó là lý do các mục ấy còn nằm trong danh sách thay vì bị xoá: distiller
    // chỉ thấy hội thoại của các lượt MỚI, nên một câu hỏi đã đóng từ mười lượt trước mà biến mất khỏi đầu
    // vào là một câu hỏi nó sẽ dựng lại y nguyên.
    private static string BuildQuestionNote(Project project)
    {
        var questions = InterviewOutlookParser.ParseOpenQuestions(project.OpenQuestions);
        if (questions.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Danh sách câu hỏi hiện có (mỗi mục gắn nhãn nhóm; mục đã đóng ghi rõ → [ĐÃ TRẢ LỜI])");
        sb.AppendLine(InterviewOutlookParser.ToTaggedText(questions));
        return sb.ToString();
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
