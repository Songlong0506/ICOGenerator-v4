namespace ICOGenerator.Application.Requirements;

public enum ApproveRequirementResult
{
    Approved,
    NoDraftDocuments,
    MissingAiDesignSpec,
    ProjectNotFound,

    // The DB changes were rolled back because promoting the draft folders on disk
    // failed (e.g. a generated .docx is open/locked). Nothing was approved, so the
    // user can safely retry once the files are released.
    PromotionFailed
}
