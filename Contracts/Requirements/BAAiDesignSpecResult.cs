using System.Text.Json.Serialization;

namespace ICOGenerator.Contracts.Requirements;

// Kết quả bước sinh AI Design Spec từ Product Brief ĐÃ DUYỆT (chạy đồng bộ khi user bấm Approve).
// Bản kỹ thuật súc tích này là input cho Developer Agent dựng POC.
public class BAAiDesignSpecResult
{
    public string AssistantMessage { get; set; } = "";

    // Hình dạng JSON trao đổi với model là PHẲNG: nội dung spec là một chuỗi ở cấp ngoài cùng. Schema cũ
    // lồng một object (`aiDesignSpec: {content}`), và khi câu trả lời đi qua tool `structured_response` thì
    // claude-sonnet-5-5 thỉnh thoảng viết object lồng đó bằng cú pháp thẻ của lời gọi tool
    // (`"aiDesignSpec": "<parameter name="content">…"`) — ca JD Library V2/V3, spec thật bị thay bằng khung
    // dự phòng. Không có object lồng thì không có chỗ cho kiểu viết đó. Dạng lồng cũ vẫn đọc được ở
    // RequirementResponseParser.TryParseAiDesignSpec (prompt bị ghi đè qua Prompt Studio có thể còn mô tả nó).
    public string AiDesignSpecContent
    {
        get => AiDesignSpec.Content;
        set => AiDesignSpec.Content = value ?? "";
    }

    // Code nội bộ (sinh file, đối chiếu Brief↔spec) vẫn đọc qua đây; không đi ra JSON.
    [JsonIgnore]
    public AiDesignSpecDto AiDesignSpec { get; set; } = new();
}
