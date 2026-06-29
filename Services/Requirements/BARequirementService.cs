using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

public class BARequirementService
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly RequirementTemplateService _templateService;
    private readonly RequirementPromptBuilder _promptBuilder;
    private readonly RequirementResponseParser _responseParser;
    private readonly BAChatReplyParser _replyParser;
    private readonly RequirementReadinessParser _readinessParser;
    private readonly RequirementDocumentGenerator _documentGenerator;
    private readonly PromptTemplateService _promptTemplateService;
    private readonly SourceContextBuilder _sourceContextBuilder;
    private readonly IProjectArtifactCatalog _artifactCatalog;

    public BARequirementService(
        AppDbContext db,
        ILlmClient llm,
        RequirementTemplateService templateService,
        RequirementPromptBuilder promptBuilder,
        RequirementResponseParser responseParser,
        BAChatReplyParser replyParser,
        RequirementReadinessParser readinessParser,
        RequirementDocumentGenerator documentGenerator,
        PromptTemplateService promptTemplateService,
        SourceContextBuilder sourceContextBuilder,
        IProjectArtifactCatalog artifactCatalog)
    {
        _db = db;
        _llm = llm;
        _templateService = templateService;
        _promptBuilder = promptBuilder;
        _responseParser = responseParser;
        _replyParser = replyParser;
        _readinessParser = readinessParser;
        _documentGenerator = documentGenerator;
        _promptTemplateService = promptTemplateService;
        _sourceContextBuilder = sourceContextBuilder;
        _artifactCatalog = artifactCatalog;
    }

    public async Task<ChatWithBAResult> ChatAsync(Guid projectId, string userMessage, CancellationToken cancellationToken = default)
    {
        // Validate the project up front: writing an AgentConversation for a non-existent project would throw an FK DbUpdateException → HTTP 500. Return a status the controller can surface.
        if (!await _db.Projects.AnyAsync(x => x.Id == projectId, cancellationToken))
            return ChatWithBAResult.ProjectNotFound;

        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst, cancellationToken);

        // A missing BA agent / model is a configuration problem, not an exceptional crash: report
        // it as a result so Chat can show a friendly message instead of a 500.
        if (ba?.AiModel == null)
            return ChatWithBAResult.BaNotConfigured;

        var model = ba.AiModel;

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "user",
            Message = userMessage,
            TokenUsed = TokenEstimator.Estimate(userMessage)
        });
        await _db.SaveChangesAsync(cancellationToken);

        // Lấy tối đa 20 lượt gần nhất để giữ ngữ cảnh mà vẫn nhẹ token.
        var recent = await _db.AgentConversations
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        recent.Reverse();

        // Tài liệu nguồn (ảnh/PDF) của project: gắn vào ĐÚNG lượt user mới nhất (một lần) để BA "thấy" khi trả lời,
        // tránh gửi lại ảnh ở mọi lượt (đốt token). Model không vision ⇒ builder chỉ trả phần text bóc từ PDF.
        var sources = await _db.ProjectSourceFiles
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
        var sourceContents = _sourceContextBuilder.Build(sources, model.SupportsVision);
        var lastUserIndex = recent.FindLastIndex(c => c.Role != "assistant");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BA/requirement-chat.v1.md"))
        };
        for (var i = 0; i < recent.Count; i++)
        {
            var c = recent[i];
            var isAssistant = c.Role == "assistant";
            // Lượt cũ của BA được "dựng lại" đúng JSON {message, suggestions}. Nếu chỉ đưa text thuần,
            // model thấy phản hồi trước của mình là văn xuôi và bắt chước → bỏ JSON từ lượt 2 trở đi,
            // mất luôn gợi ý. Đưa lại đúng format giúp model giữ JSON ở mọi lượt.
            var text = isAssistant ? BuildAssistantContext(c) : c.Message;

            if (!isAssistant && i == lastUserIndex && sourceContents.Count > 0)
            {
                var contents = new List<AIContent> { new TextContent(text) };
                contents.AddRange(sourceContents);
                messages.Add(new ChatMessage(ChatRole.User, contents));
            }
            else
            {
                messages.Add(new ChatMessage(isAssistant ? ChatRole.Assistant : ChatRole.User, text));
            }
        }

        // BA được nhắc trả JSON {message, suggestions}: dùng structured output khi model được bật, ngược lại
        // parser luôn fallback an toàn về text thuần.
        var (callResult, structuredReply) = await _llm.ChatStructuredAsync<BAChatReply>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAChat"), cancellationToken: cancellationToken);

        // Surface a failure as a clearly-labelled assistant turn instead of a 500, but never present an API error as if it were a normal BA answer.
        string reply;
        string? suggestionsJson = null;
        if (!callResult.IsSuccess)
        {
            reply = $"⚠️ Lời gọi AI thất bại, chưa thể trả lời. Chi tiết: {callResult.ErrorMessage ?? callResult.Content}";
        }
        else
        {
            var parsedReply = structuredReply ?? _replyParser.Parse(callResult.Content);
            reply = string.IsNullOrWhiteSpace(parsedReply.Message)
                ? "Đã ghi nhận. Bạn có thể bổ sung thêm yêu cầu, hoặc bấm \"Write Requirement\" để tạo tài liệu."
                : parsedReply.Message;

            // Lưu suggestions tách riêng (JSON) để UI render chip; chỉ set khi thực sự có gợi ý.
            if (parsedReply.Suggestions.Count > 0)
                suggestionsJson = JsonSerializer.Serialize(parsedReply.Suggestions);
        }

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "assistant",
            Message = reply,
            Suggestions = suggestionsJson,
            TokenUsed = TokenEstimator.Estimate(reply)
        });
        await _db.SaveChangesAsync(cancellationToken);

        return ChatWithBAResult.Ok;
    }

    /// <param name="onProgress">Callback (kind, message, detail) báo tiến độ live cho UI; có thể null khi gọi đồng bộ.</param>
    /// <param name="onToken">Callback nhận từng token nội dung khi model soạn tài liệu, để stream "đang gõ" lên UI.</param>
    /// <param name="workflowRunId">Run liên quan để gắn chi phí token vào đúng workflow run (null nếu gọi ngoài workflow).</param>
    public async Task<RequirementDraftOutcome> GenerateOrUpdateDraftAsync(Guid projectId, Action<string, string, string?>? onProgress = null, Action<string>? onToken = null, Guid? workflowRunId = null, CancellationToken cancellationToken = default)
    {
        void Report(string kind, string message, string? detail = null) => onProgress?.Invoke(kind, message, detail);

        Report("setup", "Đang đọc hội thoại…");

        var project = await _db.Projects
            .Include(x => x.Documents)
            .Include(x => x.Conversations)
            .Include(x => x.SourceFiles)
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project not found: {projectId}.");

        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst, cancellationToken)
            ?? throw new InvalidOperationException(
                "Chưa cấu hình BA agent (RoleKey = BusinessAnalyst). Hãy tạo hoặc khôi phục agent BA trong màn hình Manage Agent.");

        var model = ba.AiModel ?? throw new InvalidOperationException("BA agent model is not configured.");

        var requirementBrief = BuildRequirementBrief(project.Conversations);

        // Tài liệu nguồn (ảnh/PDF) của project → AIContent gắn kèm lượt soạn tài liệu (text PDF + ảnh nếu model vision).
        var sources = project.SourceFiles.OrderBy(s => s.CreatedAt).ToList();
        var sourceContents = _sourceContextBuilder.Build(sources, model.SupportsVision);

        // Cổng kiểm tra: nếu còn thiếu thông tin CỐT LÕI thì hỏi lại NGAY (một lượt BA trong khung chat)
        // và KHÔNG soạn 5 tài liệu — tránh sinh tài liệu rồi vứt đi/sinh lại (tốn token). Đây là một lời
        // gọi LLM nhẹ (chỉ trả câu hỏi). Kèm tóm tắt text tài liệu nguồn để readiness tính cả tài liệu đính kèm.
        Report("thinking", "Đang kiểm tra mức độ đầy đủ của yêu cầu…", requirementBrief);
        var readiness = await CheckReadinessAsync(projectId, ba, model, requirementBrief + BuildSourceBriefNote(sources), cancellationToken);
        if (!readiness.Ready)
        {
            var question = string.IsNullOrWhiteSpace(readiness.Message)
                ? "Mình cần thêm vài thông tin cốt lõi trước khi viết tài liệu. Bạn bổ sung giúp nhé."
                : readiness.Message;
            var pendingSuggestions = readiness.Suggestions.Count > 0
                ? JsonSerializer.Serialize(readiness.Suggestions)
                : null;

            _db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = projectId,
                AgentId = ba.Id,
                Role = "assistant",
                Message = question,
                Suggestions = pendingSuggestions,
                TokenUsed = TokenEstimator.Estimate(question)
            });
            await _db.SaveChangesAsync(cancellationToken);

            Report("final", "Cần bổ sung thông tin trước khi sinh tài liệu — xem câu hỏi trong khung chat.", question);
            return RequirementDraftOutcome.NeedsMoreInfo;
        }

        Report("thinking", "Đang tổng hợp yêu cầu từ hội thoại…", requirementBrief);

        var prompt = _promptBuilder.BuildProductBrief(
            project,
            requirementBrief,
            GetDoc(project, _artifactCatalog.ProductBrief.FileName, "draft"));

        // Lượt user mang prompt soạn tài liệu + tài liệu nguồn (text/ảnh) đính kèm. Không có nguồn ⇒ chỉ một
        // TextContent, tương đương đường cũ.
        var userContents = new List<AIContent> { new TextContent(prompt) };
        userContents.AddRange(sourceContents);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BA/product-brief.v1.md")),
            new(ChatRole.User, userContents)
        };

        Report("tool", "Đang gọi AI để soạn bản mô tả sản phẩm (Product Brief)…");

        var (callResult, structuredDraft) = await _llm.ChatStructuredAsync<BAProductBriefResult>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAProductBrief", workflowRunId), onToken, cancellationToken);

        // On a failed call, do NOT fall through to the template fallback: it would fabricate documents from the raw user message and report success, hiding the failure. Fail the task instead.
        if (!callResult.IsSuccess)
        {
            var detail = callResult.ErrorMessage ?? callResult.Content;
            Report("error", "Lời gọi LLM thất bại.", detail);
            throw new InvalidOperationException($"LLM call failed: {detail}");
        }

        Report("observation", "AI đã trả về nội dung, đang phân tích kết quả…");

        // Structured output (when enabled) yields a typed result; otherwise parse the text. Both go through
        // the same normalization so downstream sees fully-populated sections.
        var result = structuredDraft != null
            ? _responseParser.Normalize(structuredDraft)
            : _responseParser.ParseProductBrief(callResult.Content, project, requirementBrief);

        Report("tool", "Đang tạo/cập nhật file tài liệu (.docx)…");

        await _documentGenerator.GenerateProductBriefDraftFiles(project, ba.Id, result);

        var assistantMessage = string.IsNullOrWhiteSpace(result.AssistantMessage)
            ? "Đã tạo/cập nhật bản mô tả sản phẩm (Product Brief) dễ hiểu cho bạn xem & duyệt."
            : result.AssistantMessage;

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "assistant",
            Message = assistantMessage,
            TokenUsed = TokenEstimator.Estimate(assistantMessage)
        });

        await _db.SaveChangesAsync(cancellationToken);

        Report("final", "Đã tạo/cập nhật tài liệu.", assistantMessage);
        return RequirementDraftOutcome.Generated;
    }

    /// <summary>
    /// Bước Approve (chạy đồng bộ): sinh AI Design Spec từ Product Brief ĐÃ DUYỆT của một phiên bản
    /// requirement (V{n}), ghi thẳng vào phiên bản đó (đã duyệt) và trả về nội dung spec để khởi động
    /// delivery workflow dựng POC. Tách khỏi "Write Requirement" để không sinh lại spec mỗi lần user
    /// chỉnh Product Brief — chỉ sinh một lần khi đã chốt requirement.
    /// </summary>
    /// <param name="onProgress">Callback (kind, message, detail) báo tiến độ live cho UI; null khi gọi đồng bộ.</param>
    /// <param name="onToken">Callback nhận từng token khi model soạn spec, để stream "đang gõ" lên UI.</param>
    /// <param name="workflowRunId">Run liên quan để gắn chi phí token vào đúng workflow run (null nếu gọi ngoài workflow).</param>
    public async Task<string> GenerateAiDesignSpecAsync(Guid projectId, string versionName, Action<string, string, string?>? onProgress = null, Action<string>? onToken = null, Guid? workflowRunId = null, CancellationToken cancellationToken = default)
    {
        void Report(string kind, string message, string? detail = null) => onProgress?.Invoke(kind, message, detail);

        Report("setup", "Đang đọc Product Brief đã duyệt…");

        var project = await _db.Projects
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project not found: {projectId}.");

        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst, cancellationToken)
            ?? throw new InvalidOperationException(
                "Chưa cấu hình BA agent (RoleKey = BusinessAnalyst). Hãy tạo hoặc khôi phục agent BA trong màn hình Manage Agent.");

        var model = ba.AiModel ?? throw new InvalidOperationException("BA agent model is not configured.");

        var productBrief = GetDoc(project, _artifactCatalog.ProductBrief.FileName, versionName);
        if (string.IsNullOrWhiteSpace(productBrief))
            throw new InvalidOperationException($"Product Brief đã duyệt không tồn tại cho phiên bản {versionName}.");

        var prompt = _promptBuilder.BuildAiDesignSpec(
            project,
            productBrief,
            GetDoc(project, _artifactCatalog.AiDesignSpec.FileName, versionName));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BA/ai-design-spec.v1.md")),
            new(ChatRole.User, prompt)
        };

        Report("tool", "Đang gọi AI để soạn AI Design Spec…");

        var (callResult, structuredSpec) = await _llm.ChatStructuredAsync<BAAiDesignSpecResult>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAAiDesignSpec", workflowRunId), onToken, cancellationToken);

        // On a failed call, do NOT fall through to a fabricated spec: surface the failure so the caller can
        // report it and the user retries, rather than silently feeding an empty spec into the POC step.
        if (!callResult.IsSuccess)
        {
            var detail = callResult.ErrorMessage ?? callResult.Content;
            Report("error", "Lời gọi LLM thất bại.", detail);
            throw new InvalidOperationException($"LLM call failed: {detail}");
        }

        Report("observation", "AI đã trả về nội dung, đang phân tích kết quả…");

        var result = structuredSpec != null
            ? _responseParser.Normalize(structuredSpec)
            : _responseParser.ParseAiDesignSpec(callResult.Content, productBrief);

        Report("tool", "Đang tạo/cập nhật file AI Design Spec (.docx)…");

        await _documentGenerator.GenerateAiDesignSpecVersionFile(project, ba.Id, versionName, result);
        await _db.SaveChangesAsync(cancellationToken);

        Report("final", $"Đã tạo AI Design Spec cho phiên bản {versionName}.");
        return result.AiDesignSpec.Content;
    }

    /// <summary>
    /// Lượt team dev trigger ở Agent Dashboard: sinh bộ tài liệu kỹ thuật (BRD/SRS/FSD/UserStories) từ
    /// Product Brief + AI Design Spec ĐÃ DUYỆT của phiên bản requirement mới nhất. Ghi thẳng vào phiên
    /// bản đó (đã duyệt) — không qua cổng draft như luồng phía user.
    /// </summary>
    public async Task GenerateTechnicalDocsAsync(Guid projectId, Action<string, string, string?>? onProgress = null, Action<string>? onToken = null, Guid? workflowRunId = null, CancellationToken cancellationToken = default)
    {
        void Report(string kind, string message, string? detail = null) => onProgress?.Invoke(kind, message, detail);

        Report("setup", "Đang đọc Product Brief & AI Design Spec đã duyệt và template tài liệu…");

        var brdTemplate = _templateService.GetBrdTemplate();
        var srsTemplate = _templateService.GetSrsTemplate();
        var fsdTemplate = _templateService.GetFsdTemplate();
        var userStoriesTemplate = _templateService.GetUserStoriesTemplate();

        var project = await _db.Projects
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project not found: {projectId}.");

        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst, cancellationToken)
            ?? throw new InvalidOperationException(
                "Chưa cấu hình BA agent (RoleKey = BusinessAnalyst). Hãy tạo hoặc khôi phục agent BA trong màn hình Manage Agent.");

        var model = ba.AiModel ?? throw new InvalidOperationException("BA agent model is not configured.");

        // Phiên bản requirement đã duyệt mới nhất (V{n}). Không có ⇒ chưa Approve, không thể sinh tài liệu kỹ thuật.
        var latestVersion = project.Documents
            .Where(x => x.IsApproved && x.VersionName.StartsWith("V"))
            .Select(x => x.VersionName)
            .OrderByDescending(v => int.TryParse(v.Replace("V", ""), out var n) ? n : 0)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Chưa có phiên bản requirement nào được duyệt. Hãy Approve requirement trước khi tạo tài liệu kỹ thuật.");

        var productBrief = GetDoc(project, _artifactCatalog.ProductBrief.FileName, latestVersion);
        var aiDesignSpec = GetDoc(project, _artifactCatalog.AiDesignSpec.FileName, latestVersion);

        Report("thinking", "Đang soạn tài liệu kỹ thuật từ Product Brief & AI Design Spec đã duyệt…");

        var prompt = _promptBuilder.BuildTechnicalDocs(
            project,
            productBrief,
            aiDesignSpec,
            GetDoc(project, "BRD.docx", latestVersion),
            GetDoc(project, "SRS.docx", latestVersion),
            GetDoc(project, "FSD.docx", latestVersion),
            GetDoc(project, "UserStories.docx", latestVersion),
            brdTemplate,
            srsTemplate,
            fsdTemplate,
            userStoriesTemplate);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BA/technical-docs.v1.md")),
            new(ChatRole.User, prompt)
        };

        Report("tool", "Đang gọi AI để soạn BRD, SRS, FSD, User Stories…");

        var (callResult, structuredDraft) = await _llm.ChatStructuredAsync<BARequirementDocxResult>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BATechnicalDocs", workflowRunId), onToken, cancellationToken);

        if (!callResult.IsSuccess)
        {
            var detail = callResult.ErrorMessage ?? callResult.Content;
            Report("error", "Lời gọi LLM thất bại.", detail);
            throw new InvalidOperationException($"LLM call failed: {detail}");
        }

        Report("observation", "AI đã trả về nội dung, đang phân tích kết quả…");

        var result = structuredDraft != null
            ? _responseParser.Normalize(structuredDraft)
            : _responseParser.Parse(callResult.Content, project, productBrief);

        Report("tool", "Đang tạo file tài liệu kỹ thuật (.docx)…");

        await _documentGenerator.GenerateTechnicalDocs(project, ba.Id, latestVersion, result);
        await _db.SaveChangesAsync(cancellationToken);

        Report("final", $"Đã tạo tài liệu kỹ thuật (BRD, SRS, FSD, User Stories) cho phiên bản {latestVersion}.", null);
    }

    // Gọi một LLM nhẹ để quyết định đã đủ thông tin cốt lõi soạn tài liệu chưa. Fail-open: gate lỗi thì
    // cứ cho qua để không chặn cứng việc sinh tài liệu.
    private async Task<RequirementReadiness> CheckReadinessAsync(Guid projectId, Agent ba, AiModel model, string requirementBrief, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BA/requirement-readiness.v1.md")),
            new(ChatRole.User, requirementBrief)
        };

        var (callResult, structuredReadiness) = await _llm.ChatStructuredAsync<RequirementReadiness>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAReadinessCheck"), cancellationToken: cancellationToken);

        if (!callResult.IsSuccess)
            return RequirementReadiness.ProceedDefault;

        return structuredReadiness ?? _readinessParser.Parse(callResult.Content);
    }

    // Dựng lại một lượt BA cũ theo đúng JSON shape mà model được yêu cầu xuất, để củng cố format ở
    // mỗi lượt. Không có việc này, model nhìn các lượt trước là văn xuôi và sẽ bỏ JSON (kèm gợi ý) từ
    // lượt thứ 2. Suggestions hỏng/cũ thì coi như mảng rỗng.
    private static string BuildAssistantContext(AgentConversation c)
    {
        var suggestions = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.Suggestions))
        {
            try
            {
                suggestions = JsonSerializer.Deserialize<List<string>>(c.Suggestions) ?? new List<string>();
            }
            catch
            {
                // Dữ liệu cũ/không hợp lệ: bỏ qua, giữ mảng rỗng.
            }
        }

        return JsonSerializer.Serialize(new { message = c.Message, suggestions });
    }

    private static string BuildRequirementBrief(IEnumerable<AgentConversation> conversations)
    {
        var userTurns = conversations
            .OrderBy(c => c.CreatedAt)
            .Where(c => c.Role != "assistant")
            .Select(c => (c.Message ?? string.Empty).Trim())
            .Where(m => m.Length > 0)
            .ToList();

        return userTurns.Count == 0
            ? "(Chưa có yêu cầu nào được ghi nhận.)"
            : string.Join("\n", userTurns.Select(m => "- " + m));
    }

    // Tóm tắt (text) tài liệu nguồn để đưa vào lời gọi readiness — vốn là call text-only nên KHÔNG kèm ảnh được;
    // bù lại nêu tên file + trích text (bóc từ PDF) có giới hạn, để gate đừng hỏi lại thứ đã có trong tài liệu.
    private static string BuildSourceBriefNote(List<ProjectSourceFile> sources)
    {
        if (sources.Count == 0)
            return string.Empty;

        const int maxCharsPerFile = 4000;
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"(Người dùng đã đính kèm {sources.Count} tài liệu nguồn: {string.Join(", ", sources.Select(s => s.FileName))}.)");
        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s.ExtractedText))
                continue;
            var text = s.ExtractedText!.Length > maxCharsPerFile
                ? s.ExtractedText[..maxCharsPerFile] + "…(đã cắt bớt)"
                : s.ExtractedText;
            sb.AppendLine($"[Nội dung trích từ {s.FileName}]");
            sb.AppendLine(text);
        }
        return sb.ToString();
    }

    private static string GetDoc(Project project, string fileName, string versionName)
    {
        return project.Documents
            .Where(x => x.VersionName == versionName && x.FileName == fileName)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.Content)
            .FirstOrDefault() ?? "";
    }
}
