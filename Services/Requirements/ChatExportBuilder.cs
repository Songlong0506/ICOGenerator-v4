using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Ảnh chụp mọi thứ cần để dựng bản xuất hội thoại. Gom thành một record để
/// <see cref="ChatExportBuilder"/> là hàm THUẦN (không đụng DB, không đụng đồng hồ toàn cục) — bản xuất
/// là thứ người dùng đem đi nhờ AI khác phân tích, nên nó phải test được từng mục một.
/// </summary>
/// <param name="Turns">Các lượt của hội thoại ĐANG dùng, đã sắp thứ tự (lượt "New Chat" lưu trữ không nằm ở đây).</param>
/// <param name="ArchivedTurnCount">Số lượt đã bị "New Chat" lưu trữ — chỉ nêu con số để người đọc biết bản xuất này không phải toàn bộ lịch sử.</param>
/// <param name="ReviewBrief">Chỉ dẫn cho AI sẽ đọc bản xuất (prompt <c>Eval/chat-review.v1.md</c>).</param>
/// <param name="SystemPrompt">Prompt hệ thống ĐANG chạy của BA — luật mà cuộc phỏng vấn phải bị chấm theo.</param>
/// <param name="OrganizationContext">
/// Khối bối cảnh tổ chức <see cref="OrganizationContextService.BuildCombinedContextAsync"/> đính vào MỌI
/// lời gọi BA (ranh giới phạm vi + nền tảng đã chốt + danh sách department/HoD + đơn vị yêu cầu). Đây là
/// NGUỒN THỨ HAI của mọi dữ kiện trong tài liệu, ngang hàng với lời người dùng — thiếu nó thì người chấm
/// đọc mọi dữ kiện đến từ đây thành "bịa thêm không có nguồn". Null khi không dựng được (fail-open).
/// </param>
public record ChatExportSnapshot(
    Project Project,
    IReadOnlyList<AgentConversation> Turns,
    int ArchivedTurnCount,
    IReadOnlyList<ProjectSourceFile> Sources,
    string? OrgUnitNote,
    string? BaAgentDescription,
    string? BaModelId,
    bool BaModelSupportsVision,
    string? UserMemory,
    string ReviewBrief,
    string SystemPrompt,
    string? OrganizationContext,
    DateTime ExportedAtUtc);

/// <summary>
/// Dựng bản xuất Markdown của một cuộc trò chuyện với BA để người dùng đem sang một AI khác (Claude,
/// ChatGPT…) nhờ rà soát chất lượng buổi phỏng vấn.
///
/// <para>
/// Vì sao không chỉ chép các bong bóng chat: thứ quyết định chất lượng của buổi phỏng vấn phần lớn KHÔNG
/// nằm trong text các bong bóng. Câu trả lời "Đúng rồi" chỉ có nghĩa khi biết BA vừa bày ra bảng cột nào;
/// một lượt hỏi GỘP có <c>Message</c> chỉ là câu dẫn, các câu hỏi thật nằm ở cột riêng; và quan trọng nhất,
/// cái mà hệ thống TIN không phải transcript mà là <b>bản đồ bao phủ</b> — nên lỗi nặng nhất (một nhóm bị
/// chấm [RÕ] oan ⇒ BA vĩnh viễn không hỏi lại) chỉ lộ ra khi đặt bản đồ CẠNH transcript. Bản xuất vì vậy
/// chở cả ba tầng: chỉ dẫn cho người chấm, trạng thái máy, và toàn văn hội thoại — cộng prompt hệ thống ở
/// phụ lục, vì "BA làm vậy có sai không" là câu hỏi không trả lời được nếu không biết BA được dặn gì.
/// </para>
///
/// <para>
/// Cùng lý do đó, phụ lục B chở <b>khối bối cảnh tổ chức</b> (xem <see cref="AppendOrganizationContext"/>):
/// nó là nguồn thứ hai của các dữ kiện trong tài liệu, và bỏ nó ra thì người chấm — vốn được dặn "mọi dữ
/// kiện phải truy ngược về lời người dùng hoặc tài liệu nguồn" — kết luận NGƯỢC HẲN với sự thật.
/// </para>
///
/// <para>Markdown (không phải JSON): đây là file để DÁN vào một cuộc chat, và người dùng cũng đọc được.</para>
/// </summary>
public static class ChatExportBuilder
{
    /// <summary>
    /// Trần ký tự trích từ text bóc của MỖI tài liệu nguồn. Nguyên văn thì một file Excel đủ sức nuốt cả
    /// bản xuất (và cũng chính là phần mà người chấm ít cần nhất — họ cần biết BA ĐÃ ĐƯỢC đọc gì, không
    /// cần đọc lại cả bảng). Với bảng tính, khối "Thống kê cột" đứng đầu text nên phần giữ lại là phần
    /// đáng giá nhất — xem SpreadsheetTextExtractor.
    /// </summary>
    public const int SourceExcerptChars = 2000;

    public static string Build(ChatExportSnapshot snapshot)
    {
        var sb = new StringBuilder();
        var project = snapshot.Project;

        sb.AppendLine($"# Bản xuất hội thoại BA — {project.Name}");
        sb.AppendLine();
        sb.AppendLine($"> Xuất từ ICOGenerator lúc {FormatTime(snapshot.ExportedAtUtc)} (giờ máy chủ). "
            + "File này chứa nội dung nghiệp vụ nội bộ và prompt hệ thống của agent — cân nhắc trước khi gửi ra ngoài.");
        sb.AppendLine();

        // Chỉ dẫn đứng TRƯỚC dữ liệu: file này sinh ra để được dán vào một cuộc chat, mà ở đó dòng đầu
        // tiên là thứ định đoạt AI kia làm gì với phần còn lại.
        sb.AppendLine(snapshot.ReviewBrief.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        AppendProject(sb, snapshot);
        AppendAgentConfig(sb, snapshot);
        AppendMachineState(sb, snapshot);
        AppendSources(sb, snapshot);
        AppendTranscript(sb, snapshot);
        AppendSystemPrompt(sb, snapshot);
        AppendOrganizationContext(sb, snapshot);

        return sb.ToString();
    }

    private static void AppendProject(StringBuilder sb, ChatExportSnapshot snapshot)
    {
        var project = snapshot.Project;

        sb.AppendLine("## 1. Bối cảnh dự án");
        sb.AppendLine();
        sb.AppendLine($"- **Tên dự án**: {OneLine(project.Name)}");
        sb.AppendLine($"- **Mô tả người dùng nhập lúc tạo**: {OneLine(project.Description, "(không có)")}");
        sb.AppendLine($"- **Đơn vị yêu cầu**: {OneLine(snapshot.OrgUnitNote, "(chưa gắn đơn vị)")}");
        sb.AppendLine($"- **Tạo lúc**: {FormatTime(project.CreatedAt)}");
        sb.AppendLine($"- **Số lượt trong hội thoại này**: {snapshot.Turns.Count}");

        // Không im lặng bỏ qua các lượt đã lưu trữ: người chấm thấy một hội thoại mở đầu giữa chừng mà
        // không biết có phần trước đó sẽ đổ lỗi nhầm cho BA ("sao không hỏi từ đầu?").
        if (snapshot.ArchivedTurnCount > 0)
        {
            sb.AppendLine($"- **Lưu ý**: {snapshot.ArchivedTurnCount} lượt của các hội thoại CŨ (đã bấm \"New Chat\") "
                + "không nằm trong bản xuất này — BA cũng không còn dùng chúng làm ngữ cảnh.");
        }

        sb.AppendLine();
    }

    private static void AppendAgentConfig(StringBuilder sb, ChatExportSnapshot snapshot)
    {
        sb.AppendLine("## 2. Cấu hình agent BA đang chạy");
        sb.AppendLine();
        sb.AppendLine($"- **Agent**: Business Analyst — {OneLine(snapshot.BaAgentDescription, "(không có mô tả)")}");
        sb.AppendLine($"- **Model**: {OneLine(snapshot.BaModelId, "(chưa cấu hình)")}");
        // Model không vision đổi hẳn cách chấm: BA "không thấy" ảnh trong tài liệu nguồn, nên một câu hỏi
        // trông như "hỏi lại điều file đã nói" thật ra là chuyện bắt buộc.
        sb.AppendLine($"- **Đọc được ảnh (vision)**: {(snapshot.BaModelSupportsVision ? "có" : "KHÔNG — mọi ảnh trong tài liệu nguồn không tới được model, chỉ phần chữ đi qua")}");
        sb.AppendLine();
    }

    private static void AppendMachineState(StringBuilder sb, ChatExportSnapshot snapshot)
    {
        var project = snapshot.Project;

        sb.AppendLine("## 3. Trạng thái máy chắt từ hội thoại");
        sb.AppendLine();
        sb.AppendLine("Đây là thứ hệ thống TIN và đem dùng ở các bước sau — đối chiếu nó với mục 5 để bắt "
            + "các kết luận không có căn cứ trong hội thoại.");
        sb.AppendLine();

        sb.AppendLine("### 3.1. Bản đồ bao phủ yêu cầu");
        sb.AppendLine();
        if (string.IsNullOrWhiteSpace(project.RequirementCoverageMap))
        {
            sb.AppendLine("(chưa có — chưa lượt chat nào được chắt lọc thành bản đồ)");
        }
        else
        {
            // Nguyên văn, không parse lại thành bảng: đây đúng là chuỗi đã đi vào prompt của BA ở lượt
            // gần nhất, kèm khối {nguồn: …} của từng dòng — người chấm cần soi chính bằng chứng đó.
            AppendFenced(sb, project.RequirementCoverageMap);
        }
        sb.AppendLine();

        // Hội thoại đi kèm để bản xuất in ra ĐÚNG câu cổng sẽ phát lượt tới: cổng đổi nhóm khi nó đã hỏi
        // nhóm đó rồi, nên bỏ tham số này là bản xuất kể một câu chặn khác với câu người dùng sẽ thấy.
        var readiness = RequirementReadinessGate.Evaluate(project.RequirementCoverageMap, project.Conversations);
        sb.AppendLine("### 3.2. Cổng sẵn sàng soạn tài liệu (suy tất định từ bản đồ trên)");
        sb.AppendLine();
        sb.AppendLine(readiness.Ready
            ? "- **Sẵn sàng**: mọi nhóm áp dụng đã [RÕ] — nút \"Write Requirement\" đang mở."
            : "- **Chưa sẵn sàng** — nút \"Write Requirement\" đang khóa.");
        if (!readiness.Ready && !string.IsNullOrWhiteSpace(readiness.Message))
            sb.AppendLine($"- Câu chặn hệ thống dựng sẵn: {OneLine(readiness.Message)}");
        sb.AppendLine();

        AppendBullets(sb, "3.3. Điều đã chốt (nhật ký quyết định BA chắt sau mỗi lượt)",
            DecisionLogService.ParseItems(project.DecisionLog),
            "(chưa có điều nào được chắt)");

        AppendBullets(sb, "3.4. Điểm cần làm rõ còn tồn đọng",
            InterviewOutlookService.ParseItems(project.OpenQuestions),
            "(không có điểm nào đang tồn đọng)");

        AppendBullets(sb, "3.5. Phạm vi dự kiến (màn hình/tính năng chắt từ hội thoại)",
            InterviewOutlookService.ParseItems(project.PlannedScope),
            "(chưa chắt được phạm vi nào)");

        AppendBullets(sb, "3.6. Ví dụ đã xác nhận (input → kết quả kỳ vọng, dùng để chấm bản demo)",
            InterviewOutlookService.ParseItems(project.WorkedExamples),
            "(chưa chốt ví dụ nào — mọi quy tắc định lượng đang thiếu ví dụ số)");

        // Bảng phân quyền ĐÃ CHỐT là bằng chứng DUY NHẤT được chấp nhận cho dòng «Phân quyền theo nghiệp
        // vụ» của bản đồ. Người chấm phải thấy nó ở đây để phân biệt hai ca trông giống hệt nhau trên
        // transcript: dòng đó [RÕ] vì người dùng đã tự chọn từng ô, hay [RÕ] oan vì một chip "Đồng ý".
        AppendText(sb, "3.7. Bảng phân quyền người dùng đã chốt (bằng chứng của dòng «Phân quyền theo nghiệp vụ»)",
            PermissionMatrixBuilder.RenderConfirmedBlock(project.PermissionMatrix),
            "(chưa chốt — dòng phân quyền của bản đồ KHÔNG được phép [RÕ] khi mục này còn trống)");

        // Bảng thông báo ĐÃ CHỐT có cùng luật một chiều với bảng phân quyền ngay trên: dòng «Thông báo /
        // nhắc nhở» của bản đồ không bao giờ được [RÕ] khi chưa có bảng này. Người chấm phải thấy nó ở đây
        // vì hai ca vẫn trông giống hệt nhau trên transcript — và ở bảng này còn ca thứ ba: bảng đã gửi
        // nhưng người dùng bỏ trống người nhận ở vài dòng, lúc đó dòng bản đồ chỉ được [MỘT PHẦN].
        AppendText(sb, "3.8. Bảng thông báo người dùng đã chốt (bằng chứng của dòng «Thông báo / nhắc nhở»)",
            NotificationMapBuilder.RenderConfirmedBlock(project.NotificationMap),
            "(chưa chốt — dòng thông báo của bản đồ KHÔNG được phép [RÕ] khi mục này còn trống)");

        AppendText(sb, "3.9. Bộ nhớ hội thoại (tóm tắt các lượt CŨ đã lược khỏi cửa sổ nguyên văn)",
            project.ConversationSummary,
            "(chưa có — hội thoại còn ngắn, mọi lượt vẫn được gửi nguyên văn)");

        AppendText(sb, "3.10. Hồ sơ người dùng (đúc kết xuyên dự án, nạp vào mọi lượt chat)",
            snapshot.UserMemory,
            "(chưa có hồ sơ)");
    }

    private static void AppendSources(StringBuilder sb, ChatExportSnapshot snapshot)
    {
        sb.AppendLine("## 4. Tài liệu nguồn người dùng đã gửi");
        sb.AppendLine();

        if (snapshot.Sources.Count == 0)
        {
            sb.AppendLine("(không có tài liệu nào — mọi thông tin đều đến từ hội thoại)");
            sb.AppendLine();
            return;
        }

        for (var i = 0; i < snapshot.Sources.Count; i++)
        {
            var source = snapshot.Sources[i];
            sb.AppendLine($"### 4.{i + 1}. {OneLine(source.FileName)}");
            sb.AppendLine();
            sb.AppendLine($"- **Loại**: {KindLabel(source.Kind)} · {source.SizeBytes / 1024} KB"
                + (source.PageCount > 0 ? $" · {source.PageCount} trang" : ""));
            sb.AppendLine($"- **Gửi lúc**: {FormatTime(source.CreatedAt)}");
            if (source.ScannedPageImageCount > 0)
                sb.AppendLine($"- **Ảnh đã lấy ra khỏi file**: {source.ScannedPageImageCount}");

            // Bảng cột đã chốt là một QUYẾT ĐỊNH của người dùng có hệ quả rất xa (nó lọc dữ liệu mẫu đi
            // vào bản kỹ thuật và bản demo), nên nó phải xem lại được trong bản xuất.
            var columns = ConversationTurnRenderer.ParseColumnMap(source.ColumnMap);
            if (columns.Count > 0)
            {
                sb.AppendLine("- **Bảng cột người dùng đã chốt**:");
                sb.AppendLine();
                sb.AppendLine("  | Cột | Dùng trong app mới | Cách hiểu |");
                sb.AppendLine("  |---|---|---|");
                foreach (var column in columns)
                    sb.AppendLine($"  | {Cell(column.Column)} | {(column.Used ? "có" : "không")} | {Cell(column.Meaning)} |");
            }

            if (!string.IsNullOrWhiteSpace(source.VisionSummary))
            {
                sb.AppendLine("- **Nội dung các hình BA đã đọc và ghi lại thành chữ**:");
                sb.AppendLine();
                AppendFenced(sb, source.VisionSummary);
            }

            if (!string.IsNullOrWhiteSpace(source.ExtractedText))
            {
                var (excerpt, truncated) = Excerpt(source.ExtractedText, SourceExcerptChars);
                sb.AppendLine($"- **Trích text hệ thống bóc ra từ file**{(truncated ? $" (cắt còn {SourceExcerptChars} ký tự đầu)" : "")}:");
                sb.AppendLine();
                AppendFenced(sb, excerpt);
            }

            sb.AppendLine();
        }
    }

    private static void AppendTranscript(StringBuilder sb, ChatExportSnapshot snapshot)
    {
        sb.AppendLine("## 5. Toàn văn hội thoại");
        sb.AppendLine();

        if (snapshot.Turns.Count == 0)
        {
            sb.AppendLine("(chưa có lượt nào)");
            sb.AppendLine();
            return;
        }

        for (var i = 0; i < snapshot.Turns.Count; i++)
        {
            var turn = snapshot.Turns[i];
            var isAssistant = ConversationTurnRenderer.IsAssistant(turn);

            sb.AppendLine($"### Lượt {i + 1} — {(isAssistant ? "BA" : "NGƯỜI DÙNG")} · {FormatTime(turn.CreatedAt)}"
                + (turn.TokenUsed > 0 ? $" · {turn.TokenUsed} token" : ""));
            sb.AppendLine();

            var message = (turn.Message ?? string.Empty).Trim();
            sb.AppendLine(message.Length > 0 ? message : "*(lượt trống)*");

            // Khối trích dẫn của các cột phụ tách khỏi đoạn văn bằng một dòng trống; lượt không có cột
            // phụ nào thì dòng trống đó là dòng ngăn với lượt sau, không cần thêm dòng nữa.
            var extrasStart = sb.Length;
            sb.AppendLine();

            var attachments = ConversationTurnRenderer.ParseAttachments(turn.Attachments);
            if (attachments.Count > 0)
                sb.AppendLine($"> 📎 **Đính kèm ở lượt này**: {string.Join(", ", attachments.Select(a => a.Name))}");

            var suggestions = ConversationTurnRenderer.ParseSuggestions(turn.Suggestions);
            if (suggestions.Count > 0)
            {
                // Kèm cả chế độ chọn: một bộ chip "chọn nhiều" gồm các phương án phủ định nhau là một lỗi
                // có thật của hệ thống, và không nhìn thấy cờ này thì không ai phát hiện được nó.
                sb.AppendLine($"> 💡 **Đáp án gợi ý đã bày ra** ({(turn.SuggestionsMultiSelect ? "chọn nhiều" : "chọn một")}): "
                    + string.Join(" · ", suggestions.Select((s, n) => $"[{n + 1}] {OneLine(s)}")));
            }

            var questions = ConversationTurnRenderer.ParseQuestions(turn.Questions);
            if (questions.Count > 0)
            {
                // Message của lượt hỏi GỘP chỉ là câu dẫn — không in khối này thì bản xuất che mất chính
                // các câu hỏi, và người chấm không có gì để đối chiếu với câu trả lời ở lượt sau.
                sb.AppendLine($"> ❓ **Thẻ hỏi gộp — {questions.Count} câu trong một lượt**:");
                for (var q = 0; q < questions.Count; q++)
                {
                    var question = questions[q];
                    var group = question.Group.Trim();
                    var flags = question.OpenEnded
                        ? "câu MỞ, không chip"
                        : question.MultiSelect ? "chọn nhiều" : "chọn một";
                    sb.AppendLine($"> {q + 1}. {(group.Length > 0 ? $"*({group})* " : "")}{OneLine(question.Question)} — **{flags}**"
                        + (question.Suggestions.Count > 0
                            ? $" — gợi ý: {string.Join(" / ", question.Suggestions.Select(OneLineSafe))}"
                            : ""));
                }
            }

            var columns = ConversationTurnRenderer.ParseColumnMap(turn.ColumnMap);
            if (columns.Count > 0)
            {
                sb.AppendLine("> 🧾 **Bảng cột BA bày ra cho người dùng tích** (dấu ✓ = BA đề xuất dùng trong app mới): "
                    + string.Join(" · ", columns.Select(c => $"{(c.Used ? "✓" : "✗")} {OneLineSafe(c.Column)}")));
            }

            // Bảng phân quyền BA bày ra: người rà soát bản xuất phải thấy ĐỀ XUẤT của BA thì mới chấm được
            // tin nhắn "đây là quyền của từng vai" ngay sau đó — nó là người dùng tự chọn từng ô, hay chỉ là
            // bảng BA điền sẵn được gửi lại nguyên trạng. Dấu ✓ đánh đúng các ô BA đã KHÓA vì có trích dẫn.
            var permissions = ConversationTurnRenderer.ParsePermissionMatrix(turn.PermissionMatrix);
            if (permissions.Count > 0)
            {
                sb.AppendLine("> 🔐 **Bảng phân quyền BA bày ra cho người dùng chọn** (✓ = ô BA khóa vì đã có trích dẫn trong hội thoại):");
                foreach (var screen in permissions.GroupBy(r => r.Screen, StringComparer.Ordinal))
                {
                    sb.AppendLine($"> - {OneLineSafe(screen.Key)}");
                    foreach (var row in screen)
                    {
                        var cells = row.Grants.Select(g =>
                            $"{(g.Locked ? "✓" : "")}{OneLineSafe(g.Role)}: {(PermissionScope.IsGranted(g.Scope) ? g.Scope : "—")}");
                        sb.AppendLine($">   · {OneLineSafe(row.Function)}: {string.Join(" | ", cells)}"
                            + (string.IsNullOrWhiteSpace(row.Condition) ? "" : $" [điều kiện: {OneLineSafe(row.Condition)}]"));
                    }
                }
            }

            var flow = ConversationTurnRenderer.ParseFlowDiagram(turn.FlowDiagram);
            if (flow.Count > 0)
            {
                sb.AppendLine("> 🔀 **Sơ đồ luồng BA vẽ để người dùng xác nhận**:");
                for (var s = 0; s < flow.Count; s++)
                {
                    var step = flow[s];
                    var actor = step.Actor.Trim();
                    var outcome = step.Outcome.Trim();
                    sb.AppendLine($"> {s + 1}. {(actor.Length > 0 ? $"**{actor}**: " : "")}{OneLineSafe(step.Action)}"
                        + (outcome.Length > 0 ? $" → {OneLineSafe(outcome)}" : ""));
                }
            }

            if (sb.Length > extrasStart + Environment.NewLine.Length)
                sb.AppendLine();
        }
    }

    private static void AppendSystemPrompt(StringBuilder sb, ChatExportSnapshot snapshot)
    {
        sb.AppendLine("## Phụ lục A. Prompt hệ thống của BA (bản ĐANG chạy)");
        sb.AppendLine();
        sb.AppendLine("Đây là luật mà cuộc phỏng vấn ở mục 5 phải bị chấm theo. Bản này đã tính cả chỉnh sửa "
            + "runtime ở Prompt Studio (nếu có). BA còn nhận thêm khối ngữ cảnh ở phụ lục B — hai phụ lục "
            + "này CỘNG LẠI mới là chuỗi đã gửi cho model.");
        sb.AppendLine();
        AppendFenced(sb, snapshot.SystemPrompt);
        sb.AppendLine();
    }

    /// <summary>
    /// Khối bối cảnh tổ chức — phần bản xuất KHÔNG được phép bỏ, dù nó chỉ là "ngữ cảnh nền".
    ///
    /// <para>
    /// Người chấm được dặn rằng mọi dữ kiện trong tài liệu phải truy ngược được về một câu người dùng thật
    /// sự nói hoặc một tài liệu nguồn họ gửi. Nhưng BA còn một nguồn thứ ba mà bản xuất trước đây không hề
    /// nhắc tới: khối này, đính vào MỌI lời gọi BA kể cả bước soạn Product Brief. Hệ quả đã gặp thật: một
    /// AI rà soát chấm "nhà máy Bosch Đồng Nai", "email là kênh thông báo duy nhất" và tên HoD trong bản mô
    /// tả là BỊA THÊM ở mức NẶNG — cả ba đều là hằng số của sản phẩm, đến từ đây, và đều là hành vi ĐÚNG.
    /// Một bản xuất khiến người chấm kết luận ngược hẳn với sự thật thì tệ hơn là không có bản xuất.
    /// </para>
    /// </summary>
    private static void AppendOrganizationContext(StringBuilder sb, ChatExportSnapshot snapshot)
    {
        sb.AppendLine("## Phụ lục B. Bối cảnh tổ chức đính vào MỌI lượt gọi BA");
        sb.AppendLine();

        if (string.IsNullOrWhiteSpace(snapshot.OrganizationContext))
        {
            // Nói rõ "không có" thay vì bỏ trắng mục: im lặng thì người chấm không phân biệt được giữa
            // "dự án này BA chạy trần" và "bản xuất quên chở phần này đi".
            sb.AppendLine("(không dựng được khối ngữ cảnh nào — dữ liệu tổ chức trống hoặc đọc template lỗi; "
                + "lượt chat cũng chạy không có phần này)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("**Đọc kỹ trước khi kết luận điều gì là \"bịa thêm\".** Khối dưới đây được hệ thống đính "
            + "vào mọi lời gọi BA — cả các lượt hỏi ở mục 5 lẫn bước soạn Product Brief từ hội thoại này. Nó "
            + "là **hằng số của SẢN PHẨM và của môi trường nhà máy**, không phải lời người dùng, và người dùng "
            + "không nhìn thấy nó.");
        sb.AppendLine();
        sb.AppendLine("Vì vậy khi chấm:");
        sb.AppendLine();
        sb.AppendLine("- Một dữ kiện trong tài liệu truy ngược được về khối này là **có nguồn hợp lệ** — "
            + "ĐỪNG báo nó là bịa thêm chỉ vì không tìm thấy người dùng nói câu đó. Phạm vi nhà máy, kênh "
            + "thông báo, tên department/HoD là các ví dụ điển hình.");
        sb.AppendLine("- Ngược lại, BA bị CẤM kể lại khối này **như thể người dùng đã nói ra** (trong câu "
            + "\"mình ghi nhận…\", trong \"Điều đã chốt\", hay như một vế của mâu thuẫn bắt người dùng phân "
            + "xử). Vi phạm điều đó mới là lỗi — và là lỗi đáng báo.");
        sb.AppendLine("- Khối này cũng liệt kê những thứ BA bị cấm hỏi vì ĐÃ CHỐT. Một câu hỏi vắng mặt trong "
            + "buổi phỏng vấn có thể là **đúng luật**, không phải thiếu sót.");
        sb.AppendLine();
        AppendFenced(sb, snapshot.OrganizationContext!);
        sb.AppendLine();
    }

    private static void AppendBullets(StringBuilder sb, string heading, IReadOnlyList<string> items, string empty)
    {
        sb.AppendLine($"### {heading}");
        sb.AppendLine();
        if (items.Count == 0)
        {
            sb.AppendLine(empty);
        }
        else
        {
            foreach (var item in items)
                sb.AppendLine($"- {OneLine(item)}");
        }
        sb.AppendLine();
    }

    private static void AppendText(StringBuilder sb, string heading, string? text, string empty)
    {
        sb.AppendLine($"### {heading}");
        sb.AppendLine();
        if (string.IsNullOrWhiteSpace(text))
            sb.AppendLine(empty);
        else
            AppendFenced(sb, text);
        sb.AppendLine();
    }

    /// <summary>
    /// Đặt một khối text NGUYÊN VĂN vào hàng rào code. Hàng rào dài hơn hàng rào dài nhất bên trong nội
    /// dung: bản đồ bao phủ / text bóc từ file hoàn toàn có thể chứa ``` và khi đó hàng rào ba dấu sẽ đóng
    /// sớm, phần còn lại tràn ra ngoài thành markdown của tài liệu.
    /// </summary>
    private static void AppendFenced(StringBuilder sb, string text)
    {
        var body = text.Replace("\r\n", "\n").TrimEnd();
        var longest = 0;
        var run = 0;
        foreach (var ch in body)
        {
            run = ch == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        sb.AppendLine(fence);
        sb.AppendLine(body);
        sb.AppendLine(fence);
    }

    private static (string Text, bool Truncated) Excerpt(string text, int max)
    {
        var body = text.Replace("\r\n", "\n").Trim();
        return body.Length <= max ? (body, false) : (body[..max] + "\n…", true);
    }

    // Ô của bảng markdown: dấu | và xuống dòng trong nội dung sẽ phá cấu trúc bảng.
    private static string Cell(string? value) =>
        OneLine(value, "—").Replace("|", "\\|");

    private static string OneLineSafe(string? value) => OneLine(value, "");

    // Gộp về một dòng: các chuỗi này được nhúng vào bullet/blockquote, một ký tự xuống dòng là đủ để
    // phần sau rơi ra ngoài khối và đọc như một đoạn văn riêng.
    private static string OneLine(string? value, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return string.Join(" ", value.Replace("\r", " ").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())).Trim();
    }

    private static string KindLabel(SourceFileKind kind) => kind switch
    {
        SourceFileKind.Image => "ảnh",
        SourceFileKind.Pdf => "PDF",
        SourceFileKind.Spreadsheet => "bảng tính",
        SourceFileKind.Document => "tài liệu Word",
        _ => kind.ToString()
    };

    private static string FormatTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
}
