using ICOGenerator.Localization;
using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using static ICOGenerator.Services.Requirements.RequirementText;

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
/// nằm trong text các bong bóng. Câu trả lời "Đúng rồi" chỉ có nghĩa khi biết BA vừa bày ra bảng nào;
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

        sb.AppendLine(Loc.T("ChatExportBuilder.Build", project.Name));
        sb.AppendLine();
        sb.AppendLine(Loc.T("ChatExportBuilder.Build.2", FormatTime(snapshot.ExportedAtUtc)));
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

        sb.AppendLine(Loc.T("ChatExportBuilder.AppendProject"));
        sb.AppendLine();
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendProject.2", OneLine(project.Name)));
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendProject.4", OneLine(project.Description, Loc.T("ChatExportBuilder.AppendProject.3"))));
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendProject.6", OneLine(snapshot.OrgUnitNote, Loc.T("ChatExportBuilder.AppendProject.5"))));
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendProject.7", FormatTime(project.CreatedAt)));
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendProject.8", snapshot.Turns.Count));

        // Không im lặng bỏ qua các lượt đã lưu trữ: người chấm thấy một hội thoại mở đầu giữa chừng mà
        // không biết có phần trước đó sẽ đổ lỗi nhầm cho BA ("sao không hỏi từ đầu?").
        if (snapshot.ArchivedTurnCount > 0)
        {
            sb.AppendLine(Loc.T("ChatExportBuilder.AppendProject.9", snapshot.ArchivedTurnCount));
        }

        sb.AppendLine();
    }

    private static void AppendAgentConfig(StringBuilder sb, ChatExportSnapshot snapshot)
    {
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendAgentConfig"));
        sb.AppendLine();
        sb.AppendLine($"- **Agent**: Business Analyst — {OneLine(snapshot.BaAgentDescription, Loc.T("ChatExportBuilder.AppendAgentConfig.2"))}");
        sb.AppendLine($"- **Model**: {OneLine(snapshot.BaModelId, Loc.T("ChatExportBuilder.AppendAgentConfig.3"))}");
        // Model không vision đổi hẳn cách chấm: BA "không thấy" ảnh trong tài liệu nguồn, nên một câu hỏi
        // trông như "hỏi lại điều file đã nói" thật ra là chuyện bắt buộc.
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendAgentConfig.6", (snapshot.BaModelSupportsVision ? Loc.T("ChatExportBuilder.AppendAgentConfig.4") : Loc.T("ChatExportBuilder.AppendAgentConfig.5"))));
        sb.AppendLine();
    }

    private static void AppendMachineState(StringBuilder sb, ChatExportSnapshot snapshot)
    {
        var project = snapshot.Project;

        sb.AppendLine(Loc.T("ChatExportBuilder.AppendMachineState"));
        sb.AppendLine();
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendMachineState.2"));
        sb.AppendLine();

        sb.AppendLine(Loc.T("ChatExportBuilder.AppendMachineState.3"));
        sb.AppendLine();
        if (string.IsNullOrWhiteSpace(project.RequirementCoverageMap))
        {
            sb.AppendLine(Loc.T("ChatExportBuilder.AppendMachineState.4"));
        }
        else
        {
            // In ra ĐÚNG 12 dòng mà BA đã đọc ở lượt gần nhất (bản đồ lưu dạng JSON, nạp vào prompt dạng
            // bullet — xem BAChatPromptBlocks.CoverageMap), kèm phần đã ghi nhận của từng dòng: người
            // chấm cần soi chính bằng chứng đó, và soi bản BA thật sự đọc chứ không phải bản lưu trữ.
            // Vì vậy KHÔNG gắn câu hỏi vào (AttachQuestions): ngữ cảnh chat cũng không gắn, và phần còn
            // treo có mục 3.3 ngay dưới — in hai lần thì người chấm đếm một mẩu hỏi thành hai việc.
            AppendFenced(sb, CoverageMapParser.ToText(CoverageMapParser.Parse(project.RequirementCoverageMap)));
        }
        sb.AppendLine();

        // Hội thoại đi kèm để bản xuất in ra ĐÚNG câu cổng sẽ phát lượt tới: cổng đổi nhóm khi nó đã hỏi
        // nhóm đó rồi, nên bỏ tham số này là bản xuất kể một câu chặn khác với câu người dùng sẽ thấy.
        var openQuestions = InterviewOutlookParser.ParseOpenQuestions(project.OpenQuestions);
        var readiness = RequirementReadinessGate.Evaluate(project.RequirementCoverageMap, openQuestions, project.Conversations);
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendMachineState.5"));
        sb.AppendLine();
        sb.AppendLine(readiness.Ready
            ? Loc.T("ChatExportBuilder.AppendMachineState.6")
            : Loc.T("ChatExportBuilder.AppendMachineState.7"));
        if (!readiness.Ready && !string.IsNullOrWhiteSpace(readiness.Message))
            sb.AppendLine(Loc.T("ChatExportBuilder.AppendMachineState.8", OneLine(readiness.Message)));
        sb.AppendLine();

        AppendBullets(sb, Loc.T("ChatExportBuilder.AppendMachineState.9"),
            openQuestions.Where(q => q.IsOpen).Select(q => q.Text).ToList(),
            Loc.T("ChatExportBuilder.AppendMachineState.10"));

        // Bản thiết kế UX: màn hình + hành động, kèm chỉ số và phát hiện thiết kế còn lại — người chấm cần thấy
        // được thiết kế đã được kiểm những gì, và tác vụ nào vừa lộ ra mà chưa được xếp chỗ.
        AppendText(sb, Loc.T("ChatExportBuilder.AppendMachineState.11"),
            UxBlueprintRenderer.RenderChatBlock(project.UxBlueprint),
            Loc.T("ChatExportBuilder.AppendMachineState.12"));

        AppendText(sb, Loc.T("ChatExportBuilder.AppendMachineState.13"),
            JoinLines(UxBlueprintRenderer.RenderMetricsLine(project.UxBlueprint), UxBlueprintRenderer.RenderFindings(project.UxBlueprint)),
            Loc.T("ChatExportBuilder.AppendMachineState.14"));

        // Bảng phân quyền ĐÃ CHỐT là bằng chứng DUY NHẤT được chấp nhận cho dòng «Phân quyền theo nghiệp
        // vụ» của bản đồ. Người chấm phải thấy nó ở đây để phân biệt hai ca trông giống hệt nhau trên
        // transcript: dòng đó [RÕ] vì người dùng đã tự chọn từng ô, hay [RÕ] oan vì một chip "Đồng ý".
        AppendText(sb, Loc.T("ChatExportBuilder.AppendMachineState.17"),
            PermissionMatrixBuilder.RenderConfirmedBlock(project.PermissionMatrix),
            Loc.T("ChatExportBuilder.AppendMachineState.18"));

        // Bảng thông báo ĐÃ CHỐT có cùng luật một chiều với bảng phân quyền ngay trên: dòng «Thông báo /
        // nhắc nhở» của bản đồ không bao giờ được [RÕ] khi chưa có bảng này. Người chấm phải thấy nó ở đây
        // vì hai ca vẫn trông giống hệt nhau trên transcript — và ở bảng này còn ca thứ ba: bảng đã gửi
        // nhưng người dùng bỏ trống người nhận ở vài dòng, lúc đó dòng bản đồ chỉ được [MỘT PHẦN].
        AppendText(sb, Loc.T("ChatExportBuilder.AppendMachineState.19"),
            NotificationMapBuilder.RenderConfirmedBlock(project.NotificationMap),
            Loc.T("ChatExportBuilder.AppendMachineState.20"));

        AppendText(sb, Loc.T("ChatExportBuilder.AppendMachineState.21"),
            project.ConversationSummary,
            Loc.T("ChatExportBuilder.AppendMachineState.22"));

        AppendText(sb, Loc.T("ChatExportBuilder.AppendMachineState.23"),
            snapshot.UserMemory,
            Loc.T("ChatExportBuilder.AppendMachineState.24"));
    }

    private static void AppendSources(StringBuilder sb, ChatExportSnapshot snapshot)
    {
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendSources"));
        sb.AppendLine();

        if (snapshot.Sources.Count == 0)
        {
            sb.AppendLine(Loc.T("ChatExportBuilder.AppendSources.2"));
            sb.AppendLine();
            return;
        }

        for (var i = 0; i < snapshot.Sources.Count; i++)
        {
            var source = snapshot.Sources[i];
            sb.AppendLine($"### 4.{i + 1}. {OneLine(source.FileName)}");
            sb.AppendLine();
            sb.AppendLine(Loc.T("ChatExportBuilder.AppendSources.3", KindLabel(source.Kind), source.SizeBytes / 1024, (source.PageCount > 0 ? $" · {source.PageCount} trang" : "")));
            sb.AppendLine(Loc.T("ChatExportBuilder.AppendSources.4", FormatTime(source.CreatedAt)));
            if (source.ScannedPageImageCount > 0)
                sb.AppendLine(Loc.T("ChatExportBuilder.AppendSources.5", source.ScannedPageImageCount));

            // Bảng cột đã chốt là một QUYẾT ĐỊNH có hệ quả rất xa (nó lọc dữ liệu mẫu đi vào bản kỹ thuật và
            // bản demo) — BA chốt, người dùng có thể đã sửa — nên nó phải xem lại được trong bản xuất.
            var columns = ConversationTurnRenderer.ParseColumnMap(source.ColumnMap);
            if (columns.Count > 0)
            {
                sb.AppendLine(Loc.T("ChatExportBuilder.AppendSources.6"));
                sb.AppendLine();
                sb.AppendLine(Loc.T("ChatExportBuilder.AppendSources.7"));
                sb.AppendLine("  |---|---|---|");
                foreach (var column in columns)
                    sb.AppendLine($"  | {Cell(column.Column)} | {(column.Used ? Loc.T("ChatExportBuilder.AppendSources.8") : Loc.T("ChatExportBuilder.AppendSources.9"))} | {Cell(column.Meaning)} |");
            }

            if (!string.IsNullOrWhiteSpace(source.VisionSummary))
            {
                sb.AppendLine(Loc.T("ChatExportBuilder.AppendSources.10"));
                sb.AppendLine();
                AppendFenced(sb, source.VisionSummary);
            }

            if (!string.IsNullOrWhiteSpace(source.ExtractedText))
            {
                var (excerpt, truncated) = Excerpt(source.ExtractedText, SourceExcerptChars);
                sb.AppendLine(Loc.T("ChatExportBuilder.AppendSources.12", (truncated ? Loc.T("ChatExportBuilder.AppendSources.11", SourceExcerptChars) : "")));
                sb.AppendLine();
                AppendFenced(sb, excerpt);
            }

            sb.AppendLine();
        }
    }

    private static void AppendTranscript(StringBuilder sb, ChatExportSnapshot snapshot)
    {
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendTranscript"));
        sb.AppendLine();

        if (snapshot.Turns.Count == 0)
        {
            sb.AppendLine(Loc.T("ChatExportBuilder.AppendTranscript.2"));
            sb.AppendLine();
            return;
        }

        for (var i = 0; i < snapshot.Turns.Count; i++)
        {
            var turn = snapshot.Turns[i];
            var isAssistant = ConversationTurnRenderer.IsAssistant(turn);

            sb.AppendLine(Loc.T("ChatExportBuilder.AppendTranscript.4", i + 1, (isAssistant ? "BA" : Loc.T("ChatExportBuilder.AppendTranscript.3")), FormatTime(turn.CreatedAt), (turn.TokenUsed > 0 ? $" · {turn.TokenUsed} token" : "")));
            sb.AppendLine();

            var message = (turn.Message ?? string.Empty).Trim();
            sb.AppendLine(message.Length > 0 ? message : Loc.T("ChatExportBuilder.AppendTranscript.5"));

            // Khối trích dẫn của các cột phụ tách khỏi đoạn văn bằng một dòng trống; lượt không có cột
            // phụ nào thì dòng trống đó là dòng ngăn với lượt sau, không cần thêm dòng nữa.
            var extrasStart = sb.Length;
            sb.AppendLine();

            var attachments = ConversationTurnRenderer.ParseAttachments(turn.Attachments);
            if (attachments.Count > 0)
                sb.AppendLine(Loc.T("ChatExportBuilder.AppendTranscript.6", string.Join(", ", attachments.Select(a => a.Name))));

            var suggestions = ConversationTurnRenderer.ParseSuggestions(turn.Suggestions);
            if (suggestions.Count > 0)
            {
                // Kèm cả chế độ chọn: một bộ chip "chọn nhiều" gồm các phương án phủ định nhau là một lỗi
                // có thật của hệ thống, và không nhìn thấy cờ này thì không ai phát hiện được nó.
                sb.AppendLine(Loc.T("ChatExportBuilder.AppendTranscript.9", (turn.SuggestionsMultiSelect ? Loc.T("ChatExportBuilder.AppendTranscript.7") : Loc.T("ChatExportBuilder.AppendTranscript.8")), string.Join(" · ", suggestions.Select((s, n) => $"[{n + 1}] {OneLine(s)}"))));
            }

            var questions = ConversationTurnRenderer.ParseQuestions(turn.Questions);
            if (questions.Count > 0)
            {
                // Message của lượt hỏi GỘP chỉ là câu dẫn — không in khối này thì bản xuất che mất chính
                // các câu hỏi, và người chấm không có gì để đối chiếu với câu trả lời ở lượt sau.
                sb.AppendLine(Loc.T("ChatExportBuilder.AppendTranscript.10", questions.Count));
                for (var q = 0; q < questions.Count; q++)
                {
                    var question = questions[q];
                    var group = question.Group.Trim();
                    var flags = question.OpenEnded
                        ? Loc.T("ChatExportBuilder.AppendTranscript.11")
                        : question.MultiSelect ? Loc.T("ChatExportBuilder.AppendTranscript.12") : Loc.T("ChatExportBuilder.AppendTranscript.13");
                    sb.AppendLine($"> {q + 1}. {(group.Length > 0 ? $"*({group})* " : "")}{OneLine(question.Question)} — **{flags}**"
                        + (question.Suggestions.Count > 0
                            ? Loc.T("ChatExportBuilder.AppendTranscript.14", string.Join(" / ", question.Suggestions.Select(OneLineSafe)))
                            : ""));
                }
            }

            var columns = ConversationTurnRenderer.ParseColumnMap(turn.ColumnMap);
            if (columns.Count > 0)
            {
                sb.AppendLine(Loc.T("ChatExportBuilder.AppendTranscript.15", string.Join(" · ", columns.Select(c => $"{(c.Used ? "✓" : "✗")} {OneLineSafe(c.Column)}"))));
            }

            // Bảng phân quyền BA bày ra: người rà soát bản xuất phải thấy ĐỀ XUẤT của BA thì mới chấm được
            // tin nhắn "đây là quyền của từng vai" ngay sau đó — nó là người dùng tự chọn từng ô, hay chỉ là
            // bảng BA điền sẵn được gửi lại nguyên trạng. Dấu ✓ đánh đúng các ô BA đã KHÓA vì có trích dẫn.
            var permissions = ConversationTurnRenderer.ParsePermissionMatrix(turn.PermissionMatrix);
            if (permissions.Count > 0)
            {
                sb.AppendLine(Loc.T("ChatExportBuilder.AppendTranscript.16"));
                foreach (var screen in permissions.GroupBy(r => r.Screen, StringComparer.Ordinal))
                {
                    sb.AppendLine($"> - {OneLineSafe(screen.Key)}");
                    foreach (var row in screen)
                    {
                        var cells = row.Grants.Select(g =>
                            $"{(g.Locked ? "✓" : "")}{OneLineSafe(g.Role)}: {(PermissionScope.IsGranted(g.Scope) ? PermissionScope.Label(g.Scope) : "—")}");
                        sb.AppendLine($">   · {OneLineSafe(row.Function)}: {string.Join(" | ", cells)}"
                            + (string.IsNullOrWhiteSpace(row.Condition) ? "" : Loc.T("ChatExportBuilder.AppendTranscript.17", OneLineSafe(row.Condition))));
                    }
                }
            }

            AppendProposedTables(sb, turn);

            // Chỉ còn với hội thoại CŨ: lượt mới không ghi cột này nữa (xem BAConversationLog).
            var flow = ConversationTurnRenderer.ParseFlowDiagram(turn.FlowDiagram);
            if (flow.Count > 0)
            {
                sb.AppendLine(Loc.T("ChatExportBuilder.AppendTranscript.18"));
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

    /// <summary>
    /// BỐN bảng chốt còn lại mà BA bày ra ở một lượt — luồng, màn hình, đối tượng nghiệp vụ, thông báo. Cùng
    /// một lý do tồn tại với khối bảng phân quyền ngay trên: người rà soát phải thấy ĐỀ XUẤT của BA thì mới
    /// chấm được tin nhắn "mình đã rà bảng…" ở lượt sau — nó là người dùng tự chọn từng dòng, hay chỉ là bảng
    /// BA điền sẵn được gửi lại nguyên trạng.
    ///
    /// <para>
    /// Không in bốn bảng này là để người chấm mù đúng ở chỗ dễ ký tên hộ người dùng nhất. Ca thật (dự án JD
    /// Library, lượt 68): bản xuất không có dòng nào cho bảng thông báo, nên không cách nào phân biệt "người
    /// dùng tự chọn To: HOD của đơn vị" với "BA điền sẵn rồi người dùng bấm gửi" — mà dòng ấy đã vào "Điều đã
    /// chốt" như một quyết định của người dùng, và về nghiệp vụ nó còn đáng ngờ (JD chờ HRBP verify mà email
    /// lại gửi cho HOD).
    /// </para>
    ///
    /// <para>
    /// Dấu <b>✓</b> chỉ đánh ở hai bảng CÓ ô khóa được (đối tượng, thông báo) — đúng chỗ một trích dẫn đổi
    /// được trạng thái dòng. Bảng luồng và bảng màn hình ra với mọi dòng đã tích sẵn nên chúng không có dấu
    /// đó (xem <c>docs/requirement-flow.md</c> § *"Vì sao bảng luồng và bảng màn hình không có dấu ✓ bằng
    /// chứng"*); ở đây in <b>✗</b> cho dòng bị bỏ tích thay vì im lặng bỏ nó khỏi bản xuất.
    /// </para>
    /// </summary>
    private static void AppendProposedTables(StringBuilder sb, AgentConversation turn)
    {
        var flows = ConversationTurnRenderer.ParseFlowMap(turn.FlowMap);
        if (flows.Count > 0)
        {
            sb.AppendLine(Loc.T("ChatExportBuilder.AppendProposedTables"));
            foreach (var row in flows)
            {
                var trigger = row.Trigger.Trim();
                sb.AppendLine($"> - {OneLineSafe(row.Name)} · {OneLineSafe(FlowKind.Label(row.Kind))}"
                    + (row.Role.Trim().Length > 0 ? $" · vai: {OneLineSafe(row.Role)}" : "")
                    + (trigger.Length > 0 ? Loc.T("ChatExportBuilder.AppendProposedTables.2", OneLineSafe(trigger)) : ""));
                for (var s = 0; s < row.Steps.Count; s++)
                {
                    var step = row.Steps[s];
                    var outcome = step.Outcome.Trim();
                    sb.AppendLine($">   {s + 1}. {(step.Included ? "" : "✗ ")}"
                        + (step.Actor.Trim().Length > 0 ? $"**{OneLineSafe(step.Actor)}**: " : "")
                        + OneLineSafe(step.Action)
                        + (outcome.Length > 0 ? $" → {OneLineSafe(outcome)}" : ""));
                }
            }
        }

        // Chỉ còn ở các buổi phỏng vấn CŨ: bảng báo cáo nay do BA ráp ngầm, không lượt nào bày nó ra nữa.
        var reports = ConversationTurnRenderer.ParseReportMap(turn.ReportMap);
        if (reports.Count > 0)
        {
            sb.AppendLine(Loc.T("ChatExportBuilder.AppendProposedTables.8"));
            foreach (var row in reports)
            {
                sb.AppendLine($"> - {Mark(false, row.Included)}{OneLineSafe(row.Report)}"
                    + (row.Question.Trim().Length > 0 ? Loc.T("ChatExportBuilder.AppendProposedTables.9", OneLineSafe(row.Question)) : "")
                    + (row.Source.Trim().Length > 0 ? Loc.T("ChatExportBuilder.AppendProposedTables.10", OneLineSafe(row.Source)) : "")
                    + (row.Breakdown.Trim().Length > 0 ? Loc.T("ChatExportBuilder.AppendProposedTables.11", OneLineSafe(row.Breakdown)) : ""));
            }
        }

        var notifications = ConversationTurnRenderer.ParseNotificationMap(turn.NotificationMap);
        if (notifications.Count > 0)
        {
            sb.AppendLine(Loc.T("ChatExportBuilder.AppendProposedTables.13"));
            foreach (var row in notifications)
            {
                // "chưa chọn" là trạng thái THẬT và là thứ đáng soi nhất của bảng này ở lượt BÀY: ô To trống
                // nghĩa là BA không có trích dẫn nào để điền, nên người dùng phải tự chọn. Chỉ còn ở buổi
                // CŨ — bảng thông báo không còn được bày ra.
                sb.AppendLine($"> - {Mark(row.Locked, row.Needed)}{OneLineSafe(NotificationMapBuilder.EventLabel(row))}"
                    + $" ⇒ To: {(row.To.Count > 0 ? string.Join(", ", row.To.Select(OneLineSafe)) : Loc.T("ChatExportBuilder.AppendProposedTables.14"))}"
                    + (row.Cc.Count > 0 ? $"; CC: {string.Join(", ", row.Cc.Select(OneLineSafe))}" : "")
                    + (row.AddedByUser ? Loc.T("ChatExportBuilder.AppendProposedTables.15") : "")
                    + Quote(row.Evidence));
            }
        }
    }

    /// <summary>Dấu đầu dòng của một dòng bảng: khóa vì có trích dẫn (✓), bị bỏ tích (✗), hay bình thường.</summary>
    private static string Mark(bool locked, bool kept)
        => !kept ? "✗ " : locked ? "✓ " : string.Empty;

    /// <summary>Trích dẫn BA khai để khóa một dòng — in ra để người chấm soi được nó có thật trong hội thoại.</summary>
    private static string Quote(string? evidence)
    {
        var text = (evidence ?? string.Empty).Trim();
        return text.Length == 0 ? string.Empty : Loc.T("ChatExportBuilder.Quote", OneLineSafe(text));
    }

    private static void AppendSystemPrompt(StringBuilder sb, ChatExportSnapshot snapshot)
    {
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendSystemPrompt"));
        sb.AppendLine();
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendSystemPrompt.2"));
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
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendOrganizationContext"));
        sb.AppendLine();

        if (string.IsNullOrWhiteSpace(snapshot.OrganizationContext))
        {
            // Nói rõ "không có" thay vì bỏ trắng mục: im lặng thì người chấm không phân biệt được giữa
            // "dự án này BA chạy trần" và "bản xuất quên chở phần này đi".
            sb.AppendLine(Loc.T("ChatExportBuilder.AppendOrganizationContext.2"));
            sb.AppendLine();
            return;
        }

        sb.AppendLine(Loc.T("ChatExportBuilder.AppendOrganizationContext.3"));
        sb.AppendLine();
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendOrganizationContext.4"));
        sb.AppendLine();
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendOrganizationContext.5"));
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendOrganizationContext.6"));
        sb.AppendLine(Loc.T("ChatExportBuilder.AppendOrganizationContext.7"));
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

    private static string? JoinLines(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return kept.Count == 0 ? null : string.Join("\n", kept);
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

    private static string KindLabel(SourceFileKind kind) => kind switch
    {
        SourceFileKind.Image => Loc.T("ChatExportBuilder.KindLabel"),
        SourceFileKind.Pdf => "PDF",
        SourceFileKind.Spreadsheet => Loc.T("ChatExportBuilder.KindLabel.2"),
        SourceFileKind.Document => Loc.T("ChatExportBuilder.KindLabel.3"),
        SourceFileKind.Html => "trang HTML",
        _ => kind.ToString()
    };

    private static string FormatTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
}
