using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

public class RequirementResponseParser
{
    // The model can return valid JSON with a null section/Content; guarantee non-null here, else downstream
    // deref throws a NullReferenceException. Shared by the text-parse path (below) and the structured-output
    // path (BARequirementService), so both yield a fully-populated result.
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
        try
        {
            var json = JsonExtractor.Extract(response);
            if (string.IsNullOrEmpty(json))
                return null;

            var result = JsonSerializer.Deserialize<BAProductBriefResult>(json, JsonDefaults.CaseInsensitive);
            return result == null ? null : Normalize(result);
        }
        catch
        {
            return null;
        }
    }

    public BAProductBriefResult ParseProductBrief(string response, Project project, string userMessage)
    {
        try
        {
            var json = JsonExtractor.Extract(response);
            var result = JsonSerializer.Deserialize<BAProductBriefResult>(json, JsonDefaults.CaseInsensitive);

            if (result != null)
                return Normalize(result);
        }
        catch
        {
            // Conservative fallback so the chat still produces a draft Product Brief.
        }

        return new BAProductBriefResult
        {
            AssistantMessage = "Tôi đã tạo bản mô tả sản phẩm dựa trên thông tin bạn cung cấp.",
            ProductBrief = new ProductBriefDto
            {
                Content = $$"""
# {{project.Name}}

## Sản phẩm này là gì?
{{userMessage}}

## Dành cho ai?
Cần làm rõ

## Người dùng làm được những gì? (các tính năng chính)
- {{userMessage}}

## Các màn hình chính
Cần làm rõ

## Luồng sử dụng điển hình
Cần làm rõ
"""
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

    public BAAiDesignSpecResult ParseAiDesignSpec(string response, string productBrief)
    {
        try
        {
            var json = JsonExtractor.Extract(response);
            var result = JsonSerializer.Deserialize<BAAiDesignSpecResult>(json, JsonDefaults.CaseInsensitive);

            if (result != null)
                return Normalize(result);
        }
        catch
        {
            // Conservative fallback so Approve still produces a usable AI Design Spec for the POC step.
        }

        return new BAAiDesignSpecResult
        {
            AssistantMessage = "Đã tạo AI Design Spec từ Product Brief đã duyệt.",
            AiDesignSpec = new AiDesignSpecDto
            {
                Content = $$"""
# AI Design Spec

## 1. Project Goal
{{productBrief}}

## 2. Target Users / Actors
Cần làm rõ

## 3. MVP Scope
{{productBrief}}

## 4. Out of Scope
Cần làm rõ

## 5. Navigation Structure
Cần làm rõ

## 6. Screens To Generate
Cần làm rõ

## 7. UI/UX Direction
Cần làm rõ

## 8. Data Model Summary
Cần làm rõ

## 9. API Expectations
Cần làm rõ

## 10. Business Rules
Cần làm rõ

## 11. Developer Instructions
- Generate a working POC.
- Build only MVP scope.
"""
            }
        };
    }

    public BARequirementDocxResult Parse(string response, Project project, string userMessage)
    {
        try
        {
            var json = JsonExtractor.Extract(response);
            var result = JsonSerializer.Deserialize<BARequirementDocxResult>(json, JsonDefaults.CaseInsensitive);

            if (result != null)
                return Normalize(result);
        }
        catch
        {
            // Use a conservative fallback so the chat still produces draft documents.
        }

        return new BARequirementDocxResult
        {
            AssistantMessage = "Tôi đã cập nhật requirement draft dựa trên thông tin bạn cung cấp.",
            Brd = new BrdDto
            {
                ProjectName = project.Name,
                ExecutiveSummary = userMessage,
                BusinessContext = "Cần làm rõ",
                ProblemStatement = userMessage,
                BusinessObjectives = "Cần làm rõ",
                InScope = userMessage,
                OutOfScope = "Cần làm rõ",
                Stakeholders = "Cần làm rõ",
                BusinessRequirements = userMessage,
                AsIsProcess = "Cần làm rõ",
                ToBeProcess = "Cần làm rõ",
                Risks = "Cần làm rõ",
                OpenQuestions = "Cần làm rõ"
            },
            Srs = new SrsDto
            {
                ProjectName = project.Name,
                Purpose = userMessage,
                Scope = userMessage,
                UserGroups = "Cần làm rõ",
                Assumptions = "Cần làm rõ",
                Constraints = "Cần làm rõ",
                FunctionalRequirements = userMessage,
                NonFunctionalRequirements = "Cần làm rõ",
                UiRequirements = "Cần làm rõ",
                ApiRequirements = "Cần làm rõ",
                DataRequirements = "Cần làm rõ",
                DeploymentRequirements = "Cần làm rõ",
                TestingRequirements = "Cần làm rõ",
                OpenIssues = "Cần làm rõ"
            },
            Fsd = new FsdDto
            {
                ProjectName = project.Name,
                ModuleScope = "Toàn hệ thống",
                Purpose = "FSD mô tả chi tiết hành vi chức năng của hệ thống dựa trên requirement đã thu thập.",
                Scope = userMessage,
                FunctionalArchitecture = userMessage,
                Actors = "Cần làm rõ",
                NavigationStructure = "Cần làm rõ",
                ScreenList = "Cần làm rõ",
                UiSpecification = "Cần làm rõ",
                FeatureDetails = userMessage,
                BusinessRules = "Cần làm rõ",
                MainFlows = "Cần làm rõ",
                AlternativeFlows = "Cần làm rõ",
                ApiReferences = "Cần làm rõ",
                DataReferences = "Cần làm rõ",
                OpenQuestions = "Cần làm rõ"
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
                Content = $$"""
# AI Design Spec

## 1. Project Goal
{{userMessage}}

## 2. Target Users / Actors
Cần làm rõ

## 3. MVP Scope
{{userMessage}}

## 4. Out of Scope
Cần làm rõ

## 5. Navigation Structure
Cần làm rõ

## 6. Screens To Generate
Cần làm rõ

## 7. UI/UX Direction
Cần làm rõ

## 8. Data Model Summary
Cần làm rõ

## 9. API Expectations
Cần làm rõ

## 10. Business Rules
Cần làm rõ

## 11. Developer Instructions
- Generate a working POC.
- Build only MVP scope.
"""
            }
        };
    }
}
