namespace ICOGenerator.Application.Evals;

// Kết quả chung cho tạo/sửa scenario (hai use case cùng bộ lỗi validate).
public enum SaveEvalScenarioResult
{
    Saved,
    NotFound,
    InvalidInput,
    UnknownPromptKey
}
