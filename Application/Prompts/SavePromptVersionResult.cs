namespace ICOGenerator.Application.Prompts;

public enum SavePromptVersionResult
{
    Saved,
    /// <summary>Nội dung trống.</summary>
    InvalidInput,
    /// <summary>Không có file dưới /Prompts và cũng chưa có phiên bản DB nào cho key này.</summary>
    UnknownPromptKey,
    /// <summary>Nội dung gửi lên trùng khít bản đang dùng — không tạo phiên bản trùng lặp.</summary>
    NoChange
}
