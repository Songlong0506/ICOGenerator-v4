namespace ICOGenerator.Services.Requirements;

// Outcome of a synchronous BA chat turn. Lives in Services (not Application) so the dependency arrow stays Application → Services.
public enum ChatWithBAResult
{
    Ok,

    // The project id posted to Chat does not exist. Returned (instead of letting an FK
    // violation throw a 500) so the controller can redirect to the project list.
    ProjectNotFound,

    // No active BA agent / AI model is configured. The synchronous Chat request has no
    // /Home/Error page, so this is surfaced as a TempData message instead of an exception.
    BaNotConfigured
}
