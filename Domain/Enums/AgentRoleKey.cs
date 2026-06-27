using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

public enum AgentRoleKey
{
    [Description("Business Analyst")]
    BusinessAnalyst = 1,
    [Description("Technical Lead")]
    TechLead = 2,
    [Description("Developer")]
    Developer = 3,
    [Description("QA Engineer")]
    Tester = 4,
    [Description("Designer")]
    UiUx = 5
}
