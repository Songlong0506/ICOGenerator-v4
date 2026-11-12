using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Tests.Requirements;

// Trọng tài GIẢ cho các test của chốt chặn: trả về đúng phán quyết test dặn, và ghi lại thứ nó được hỏi.
//
// Các chốt chặn của buổi phỏng vấn không còn tự đoán NGHĨA bằng bảng cụm từ — "câu này hỏi lại chưa",
// "chip này có chở câu trả lời không" là phán quyết của InterviewJudge (một lời gọi LLM). Test của chốt
// chặn vì thế ghim việc THI HÀNH một phán quyết; chất lượng của bản thân phán quyết là việc của prompt
// turn-judge.v1.md / coverage-question-judge.v1.md và của Prompt Evals.
internal sealed class StubInterviewJudge : InterviewJudge
{
    public StubInterviewJudge() : base(null!, null!) { }

    public TurnJudgeVerdict Turn = new();
    public CoverageQuestionVerdict Coverage = new();

    public int TurnCalls;
    public int CoverageCalls;
    public IReadOnlyList<string> LastAskedBefore = Array.Empty<string>();
    public IReadOnlyList<TurnQuestion> LastTurnQuestions = Array.Empty<TurnQuestion>();
    public string? LastUserMessage;
    public bool LastSummaryCheck;
    public IReadOnlyList<OpenQuestionEntry> LastOpenQuestions = Array.Empty<OpenQuestionEntry>();
    public IReadOnlyList<CoverageMapItem> LastItems = Array.Empty<CoverageMapItem>();

    public override Task<TurnJudgeVerdict> JudgeTurnAsync(
        Guid projectId, IReadOnlyList<string> askedBefore, string? lastUserMessage,
        IReadOnlyList<TurnQuestion> questions, bool summaryCheck, Agent ba, AiModel model,
        CancellationToken cancellationToken = default)
    {
        TurnCalls++;
        LastAskedBefore = askedBefore.ToList();
        LastTurnQuestions = questions.ToList();
        LastUserMessage = lastUserMessage;
        LastSummaryCheck = summaryCheck;
        return Task.FromResult(Turn);
    }

    public override Task<CoverageQuestionVerdict> JudgeOpenQuestionsAsync(
        Guid projectId, IReadOnlyList<CoverageMapItem> items, IReadOnlyList<OpenQuestionEntry> questions,
        Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        CoverageCalls++;
        LastItems = items.ToList();
        LastOpenQuestions = questions.ToList();
        return Task.FromResult(Coverage);
    }
}
