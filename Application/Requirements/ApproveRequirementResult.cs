namespace ICOGenerator.Application.Requirements;

public enum ApproveRequirementResult
{
    Approved,
    NoDraftDocuments,
    MissingAiDesignSpec,
    ProjectNotFound,

    // DB changes rolled back because promoting the draft folders on disk failed (e.g. a .docx is
    // locked). Nothing was approved, so the user can safely retry once the files are released.
    PromotionFailed,

    // Documents WERE approved and persisted, but kicking off the delivery workflow failed. The
    // approval stands; only the POC pipeline didn't start, so retry it without re-approving.
    WorkflowStartFailed
}
