using ICOGenerator.Localization;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

public class RequirementResponseParser
{
    // The model can return valid JSON with a null section/Content; guarantee non-null here, else downstream
    // deref throws a NullReferenceException. Shared by the text-parse path (below) and the structured-output
    // path (RequirementDocsService), so both yield a fully-populated result.
    public BARequirementDocxResult Normalize(BARequirementDocxResult result)
    {
        result.Brd ??= new();
        result.Srs ??= new();
        result.Fsd ??= new();
        result.UserStories ??= new();
        result.AiDesignSpec ??= new();
        result.UserStories.Content ??= "";
        result.AiDesignSpec.Content ??= "";
        return result;
    }

    // Giữ số gợi ý kèm câu hỏi làm rõ ở mức chip UI chịu được — cùng giới hạn với BAChatReplyParser.
    private const int MaxClarifyingSuggestions = 6;
    private const int MaxClarifyingSuggestionLength = 200;

    // Product Brief path (lượt "Write Requirement" phía user).
    public BAProductBriefResult Normalize(BAProductBriefResult result)
    {
        result.ProductBrief ??= new();
        result.ProductBrief.Content ??= "";
        result.ClarifyingQuestion = (result.ClarifyingQuestion ?? "").Trim();
        result.ClarifyingSuggestions = CleanClarifyingSuggestions(result.ClarifyingSuggestions);
        return result;
    }

    // Van thoát needsClarification đẩy suggestions thẳng lên UI như chip chat, nên làm sạch theo cùng
    // chuẩn với lượt chat: bỏ mục rỗng/trùng/quá dài, chặn trên số lượng.
    private static List<string> CleanClarifyingSuggestions(List<string>? raw)
    {
        var result = new List<string>();
        if (raw == null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in raw)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var trimmed = value.Trim();
            if (trimmed.Length > MaxClarifyingSuggestionLength || !seen.Add(trimmed))
                continue;

            result.Add(trimmed);
            if (result.Count >= MaxClarifyingSuggestions)
                break;
        }

        return result;
    }

    // Bản STRICT cho vòng SỬA sau tự soát: parse hỏng thì trả null để caller GIỮ bản nháp đầu. Tuyệt
    // đối không dùng fallback template ở đường này — nó sẽ ghi đè một bản nháp tốt bằng khung "Cần làm rõ".
    public BAProductBriefResult? TryParseProductBrief(string response)
    {
        var result = LlmJson.TryDeserialize<BAProductBriefResult>(response);
        return result == null ? null : Normalize(result);
    }

    public BAProductBriefResult ParseProductBrief(string response, Project project, string userMessage)
    {
        // Đọc được thì dùng; không đọc được thì rơi xuống khung dự phòng bên dưới để lượt chat vẫn ra được
        // một bản nháp Product Brief.
        if (LlmJson.TryDeserialize<BAProductBriefResult>(response) is { } result)
            return Normalize(result);

        return new BAProductBriefResult
        {
            AssistantMessage = Loc.T("RequirementResponseParser.ParseProductBrief"),
            ProductBrief = new ProductBriefDto
            {
                Content = Loc.T("RequirementResponseParser.ParseProductBrief.2", project.Name, userMessage)
            }
        };
    }

    // AI Design Spec path (sinh từ Product Brief đã duyệt khi user bấm Approve).
    public BAAiDesignSpecResult Normalize(BAAiDesignSpecResult result)
    {
        result.AiDesignSpec ??= new();
        result.AiDesignSpec.Content ??= "";
        return result;
    }

    /// <summary>
    /// Đọc câu trả lời AI Design Spec. Trả null khi không lấy ra được NỘI DUNG spec — và đường spec không có
    /// khung dự phòng nào: caller hỏi lại model hoặc báo lỗi, không bao giờ tự dựng một bản spec.
    ///
    /// <para>
    /// Vì sao không còn khung dự phòng: spec là đầu vào DUY NHẤT của bước dựng POC, và khung dự phòng (chép
    /// lại Product Brief, các mục còn lại ghi "Cần làm rõ") không parse ra màn hình/rule nào nên đối chiếu
    /// Brief↔spec cũng im lặng cho qua. Ca JD Library: hai phiên bản spec liền (V2, V3) là khung dự phòng,
    /// được người dùng duyệt và dựng POC mà không có gì báo — trong khi spec thật model đã viết nằm ngay
    /// trong phản hồi, chỉ bọc sai (xem LlmJson.RepairLeakedParameterTags).
    /// </para>
    ///
    /// <para>
    /// Đọc được cả hình dạng phẳng hiện hành (<c>aiDesignSpecContent</c>) lẫn dạng lồng cũ
    /// (<c>aiDesignSpec: {content}</c>) — prompt bị ghi đè qua Prompt Studio có thể vẫn mô tả dạng cũ.
    /// Vòng sửa sau đối chiếu Brief↔spec dùng chung hàm này: đọc hỏng ⇒ GIỮ bản vòng đầu.
    /// </para>
    /// </summary>
    public BAAiDesignSpecResult? TryParseAiDesignSpec(string response)
    {
        if (LlmJson.TryDeserialize<BAAiDesignSpecResult>(response) is { } flat && HasContent(flat.AiDesignSpecContent))
            return Normalize(flat);

        if (LlmJson.TryDeserialize<NestedAiDesignSpecResult>(response) is { AiDesignSpec.Content: var nested } legacy && HasContent(nested))
            return Normalize(new BAAiDesignSpecResult { AssistantMessage = legacy.AssistantMessage ?? "", AiDesignSpecContent = nested });

        return null;
    }

    /// <summary>
    /// Kết quả structured output (đã deserialize sẵn ở tầng LLM) nếu nó có nội dung, không thì đọc lại từ
    /// text — dạng lồng cũ đi qua structured output thành một object có <c>assistantMessage</c> mà nội dung
    /// rỗng, trông như thành công.
    /// </summary>
    public BAAiDesignSpecResult? ReadAiDesignSpec(BAAiDesignSpecResult? structured, string response) =>
        structured != null && HasContent(structured.AiDesignSpecContent)
            ? Normalize(structured)
            : TryParseAiDesignSpec(response);

    private static bool HasContent(string? content) => !string.IsNullOrWhiteSpace(content);

    // Hình dạng lồng cũ của câu trả lời spec — chỉ để ĐỌC (xem TryParseAiDesignSpec).
    private sealed class NestedAiDesignSpecResult
    {
        public string? AssistantMessage { get; set; }
        public AiDesignSpecDto? AiDesignSpec { get; set; }
    }

    public BARequirementDocxResult Parse(string response, Project project, string userMessage)
    {
        // Không đọc được thì dùng khung dự phòng bên dưới, để lượt chat vẫn ra được bộ tài liệu nháp.
        if (LlmJson.TryDeserialize<BARequirementDocxResult>(response) is { } result)
            return Normalize(result);

        return new BARequirementDocxResult
        {
            AssistantMessage = Loc.T("RequirementResponseParser.Parse"),
            Brd = new BrdDto
            {
                ProjectName = project.Name,
                ExecutiveSummary = userMessage,
                BusinessContext = Loc.T("RequirementResponseParser.Parse.2"),
                ProblemStatement = userMessage,
                BusinessObjectives = Loc.T("RequirementResponseParser.Parse.3"),
                InScope = userMessage,
                OutOfScope = Loc.T("RequirementResponseParser.Parse.4"),
                Stakeholders = Loc.T("RequirementResponseParser.Parse.5"),
                BusinessRequirements = userMessage,
                AsIsProcess = Loc.T("RequirementResponseParser.Parse.6"),
                ToBeProcess = Loc.T("RequirementResponseParser.Parse.7"),
                Risks = Loc.T("RequirementResponseParser.Parse.8"),
                OpenQuestions = Loc.T("RequirementResponseParser.Parse.9")
            },
            Srs = new SrsDto
            {
                ProjectName = project.Name,
                Purpose = userMessage,
                Scope = userMessage,
                UserGroups = Loc.T("RequirementResponseParser.Parse.10"),
                Assumptions = Loc.T("RequirementResponseParser.Parse.11"),
                Constraints = Loc.T("RequirementResponseParser.Parse.12"),
                FunctionalRequirements = userMessage,
                NonFunctionalRequirements = Loc.T("RequirementResponseParser.Parse.13"),
                UiRequirements = Loc.T("RequirementResponseParser.Parse.14"),
                ApiRequirements = Loc.T("RequirementResponseParser.Parse.15"),
                DataRequirements = Loc.T("RequirementResponseParser.Parse.16"),
                DeploymentRequirements = Loc.T("RequirementResponseParser.Parse.17"),
                TestingRequirements = Loc.T("RequirementResponseParser.Parse.18"),
                OpenIssues = Loc.T("RequirementResponseParser.Parse.19")
            },
            Fsd = new FsdDto
            {
                ProjectName = project.Name,
                ModuleScope = Loc.T("RequirementResponseParser.Parse.20"),
                Purpose = Loc.T("RequirementResponseParser.Parse.21"),
                Scope = userMessage,
                FunctionalArchitecture = userMessage,
                Actors = Loc.T("RequirementResponseParser.Parse.22"),
                NavigationStructure = Loc.T("RequirementResponseParser.Parse.23"),
                ScreenList = Loc.T("RequirementResponseParser.Parse.24"),
                UiSpecification = Loc.T("RequirementResponseParser.Parse.25"),
                FeatureDetails = userMessage,
                BusinessRules = Loc.T("RequirementResponseParser.Parse.26"),
                MainFlows = Loc.T("RequirementResponseParser.Parse.27"),
                AlternativeFlows = Loc.T("RequirementResponseParser.Parse.28"),
                ApiReferences = Loc.T("RequirementResponseParser.Parse.29"),
                DataReferences = Loc.T("RequirementResponseParser.Parse.30"),
                OpenQuestions = Loc.T("RequirementResponseParser.Parse.31")
            },
            UserStories = new UserStoriesDto
            {
                Content = $$"""
# User Stories

## US-001
As a user,
I want {{userMessage}},
so that I can achieve my business goal.

Acceptance Criteria:
- Given the user has access to the system
- When the user performs the required action
- Then the system should respond correctly
"""
            },
            AiDesignSpec = new AiDesignSpecDto
            {
                Content = Loc.T("RequirementResponseParser.Parse.32", userMessage)
            }
        };
    }
}
