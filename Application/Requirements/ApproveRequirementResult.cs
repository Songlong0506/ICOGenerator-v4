namespace ICOGenerator.Application.Requirements;

public enum ApproveRequirementResult
{
    Approved,
    NoDraftDocuments,
    MissingProductBrief,
    ProjectNotFound,

    // DB changes rolled back because promoting the draft folders on disk failed (e.g. a .docx is
    // locked). Nothing was approved, so the user can safely retry once the files are released.
    PromotionFailed,

    // Documents WERE approved and persisted, but kicking off the background AI Design Spec workflow
    // failed. The approval stands; only the spec/POC pipeline didn't start, so retry without re-approving.
    WorkflowStartFailed
}
