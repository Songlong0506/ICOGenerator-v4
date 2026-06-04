using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

public class RequirementResponseParser
{
    public BARequirementDocxResult Parse(string response, Project project, string userMessage)
    {
        try
        {
            var json = JsonExtractor.Extract(response);
            var result = JsonSerializer.Deserialize<BARequirementDocxResult>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result != null)
                return result;
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
