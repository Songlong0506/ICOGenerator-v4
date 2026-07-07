namespace ICOGenerator.Application.Evals;

// Kết quả chung cho tạo/sửa lịch eval định kỳ (hai use case cùng bộ lỗi validate).
public enum SaveEvalScheduleResult
{
    Saved,
    NotFound,
    InvalidInput,
    UnknownPromptKey,
    TargetModelNotFound,
    JudgeModelNotFound
}
