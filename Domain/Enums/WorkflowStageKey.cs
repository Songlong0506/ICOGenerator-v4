namespace ICOGenerator.Domain.Enums;

public enum WorkflowStageKey
{
    RequirementApproved = 1,
    Implementation = 2,
    Completed = 3,
    Failed = 4,
    ArchitectureDesign = 5,
    Testing = 6,
    PocPreview = 7,
    BugFix = 8,
    CodeReview = 9,
    PullRequest = 10,
    TechnicalDocs = 11,
    // Vòng sửa lỗi BIÊN DỊCH quanh bước Implementation — cổng build của worker (không phải agent tự
    // chạy) chấm code vừa sinh; đỏ thì giao lại Developer sửa. Cùng dạng "chu trình ngoài chuỗi tuyến
    // tính" như BugFix nên KHÔNG nằm trong DeliveryPipeline.Steps.
    BuildFix = 12
}
