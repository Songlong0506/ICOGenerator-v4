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
    UiUx = 5,
    // Vai NGOÀI delivery pipeline: chỉ chạy từ màn hình thử nghiệm /WebPilot, không có bước nào trong
    // DeliveryPipeline.Steps trỏ tới nó. Xem docs/agents-and-tools.md.
    [Description("Web Pilot")]
    WebPilot = 6
}
