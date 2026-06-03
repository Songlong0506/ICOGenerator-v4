using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

public class RequirementPromptBuilder
{
    public string Build(
     Project project,
     string userMessage,
     string currentBrd,
     string currentSrs,
     string currentFsd,
     string currentStories,
     string currentAiDesignSpec,
     string brdTemplate,
     string srsTemplate,
     string fsdTemplate,
     string userStoriesTemplate)
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}

User latest message:
{{userMessage}}

Current BRD preview:
{{currentBrd}}

Current SRS preview:
{{currentSrs}}

Current FSD preview:
{{currentFsd}}

Current UserStories preview:
{{currentStories}}

Current AI Design Spec preview:
{{currentAiDesignSpec}}

Company BRD Template:
{{brdTemplate}}

Company SRS Template:
{{srsTemplate}}

Company FSD Template:
{{fsdTemplate}}

Company UserStories Template:
{{userStoriesTemplate}}

Your task:
- Update BRD.docx structured data based on Company BRD Template.
- Update SRS.docx structured data based on Company SRS Template.
- Update FSD.docx structured data based on Company FSD Template.
- Update UserStories.docx content.
- Generate or update AIDesignSpec.docx content.

FSD rules:
- FSD must focus on functional behavior.
- Include navigation structure.
- Include screen hierarchy.
- Include feature details.
- Include actors and permissions.
- Include main flows and alternative flows.
- Include UI/API/Data references.
- Do not describe low-level implementation.

AI Design Spec rules:
- AIDesignSpec is the ONLY document that will be sent to Developer Agent after approval.
- It must be compact, clear, and optimized for AI code generation.
- It must include enough information to generate a POC/mockup.
- It should summarize BRD/SRS/FSD/UserStories into developer-ready context.
- Do not include unnecessary business background.
- Do not include long legal/project management sections.

AIDesignSpec must include these sections:

# AI Design Spec

## 1. Project Goal
Short summary of what the system must achieve.

## 2. Target Users / Actors
List user roles and what they can do.

## 3. MVP Scope
What must be built in the POC.

## 4. Out of Scope
What should not be built now.

## 5. Navigation Structure
Sidebar / top menu / child tabs.

Example:
- Projects
  - Master List
  - Training Plan
  - Implementation
  - Training Calendar
- Reports
- Settings
  - Training Catalog
  - System Settings

## 6. Screens To Generate
For each screen:
- Screen name
- Route URL
- Purpose
- Main components
- Table columns if any
- Form fields if any
- Buttons/actions
- Validation rules
- Empty/loading/error states

## 7. UI/UX Direction
Describe visual style:
- Enterprise dashboard
- Left sidebar
- Cards
- Tables
- Modal create/edit
- Status badges
- Responsive behavior

## 8. Data Model Summary
List main entities and important fields.

## 9. API Expectations
List expected endpoints at high level.
Do not over-engineer.

## 10. Business Rules
Only rules required for POC behavior.

## 11. Developer Instructions
Tell Developer Agent:
- Generate clean source code.
- Prioritize working POC.
- Use simple architecture.
- Build only MVP scope.
- Do not generate unnecessary modules.
- Run build/test if tools are available.

General rules:
- Keep the same section order as the templates.
- Fill unknown sections with "TBD" or "Cần làm rõ".
- Ask user for missing important information in assistantMessage.
- Do NOT write source code.
- Do NOT generate implementation files.
- Do NOT call tools.
- Return JSON only.
""";
    }

}
