namespace ICOGenerator.Application.Requirements;

public enum ApproveRequirementResult
{
    Approved,
    NoDraftDocuments,
    MissingProductBrief,
    ProjectNotFound,

    // Tài liệu đã được duyệt và promote, nhưng sinh AI Design Spec từ Product Brief (lời gọi LLM) thất
    // bại. Phiên bản vẫn đứng vững; chỉ cần retry bước sinh spec + workflow, không cần duyệt lại.
    AiDesignSpecGenerationFailed,

    // DB changes rolled back because promoting the draft folders on disk failed (e.g. a .docx is
    // locked). Nothing was approved, so the user can safely retry once the files are released.
    PromotionFailed,

    // Documents WERE approved and persisted, but kicking off the delivery workflow failed. The
    // approval stands; only the POC pipeline didn't start, so retry it without re-approving.
    WorkflowStartFailed
}
