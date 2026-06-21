namespace ICOGenerator.Contracts.Requirements;

// Kết quả "cổng kiểm tra đầy đủ" chạy TRƯỚC khi soạn 5 tài liệu. Khi Ready=false, Message + Suggestions
// được đẩy vào khung chat như một lượt BA để người dùng bổ sung, và bước sinh tài liệu bị bỏ qua
// (tránh sinh tài liệu rồi vứt → tốn token).
public class RequirementReadiness
{
    public bool Ready { get; set; }

    public string Message { get; set; } = "";

    public List<string> Suggestions { get; set; } = new();

    // Fail-open: khi không xác định được (gọi gate lỗi / parse hỏng) thì cho phép sinh tài liệu để
    // không bao giờ chặn cứng luồng vì một lỗi phụ.
    public static RequirementReadiness ProceedDefault => new() { Ready = true };
}
