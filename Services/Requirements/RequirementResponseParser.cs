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

    // Bản STRICT cho VÒNG SỬA sau đối chiếu Brief↔spec, cùng kỷ luật với TryParseProductBrief: parse
    // hỏng thì trả null để caller GIỮ bản spec vòng đầu. Tuyệt đối không dùng khung dự phòng ở đường này
    // — vòng sửa phải xuất lại TOÀN BỘ spec nên nó là output DÀI NHẤT của cả tuyến, và một phản hồi bị
    // cắt giữa chừng (ngoặc không cân ⇒ LlmJson trả null) sẽ lấy khung "Cần làm rõ" ĐÈ LÊN một bản spec
    // đang dùng được. Đầu vào duy nhất của bước dựng POC bị thay bằng bản chép lại Product Brief mà
    // không có gì báo: đối chiếu ở vòng sau vẫn kêu "còn lệch" y như một spec thật còn thiếu mục.
    public BAAiDesignSpecResult? TryParseAiDesignSpec(string response)
    {
        var result = LlmJson.TryDeserialize<BAAiDesignSpecResult>(response);
        return result == null ? null : Normalize(result);
    }

    public BAAiDesignSpecResult ParseAiDesignSpec(string response, string productBrief)
    {
        // Không đọc được thì dùng khung dự phòng bên dưới, để Approve vẫn ra được AI Design Spec dùng được
        // cho bước POC.
        //
        // Khung này giữ mục "## 10. Assumptions" với một bullet nói đúng tình trạng của nó: bản demo được
        // dựng từ một bản spec KHÔNG AI VIẾT, và đối chiếu Brief↔spec cũng không đỡ được (khung này không
        // parse ra màn hình/rule/AC nào nên SpecBriefParityChecker fail-open, im lặng luôn). Người đọc spec
        // phải thấy được điều đó thay vì một mục 10 trống trơn đọc lên như "spec này không giả định gì".
        if (LlmJson.TryDeserialize<BAAiDesignSpecResult>(response) is { } result)
            return Normalize(result);

        return new BAAiDesignSpecResult
        {
            AssistantMessage = Loc.T("RequirementResponseParser.ParseAiDesignSpec"),
            AiDesignSpec = new AiDesignSpecDto
            {
                Content = Loc.T("RequirementResponseParser.ParseAiDesignSpec.2", productBrief)
            }
        };
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
