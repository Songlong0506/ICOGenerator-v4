namespace ICOGenerator.Domain.Enums;

// Giá trị số cố định (lưu xuống DB): giữ nguyên số của các trạng thái còn dùng, không đánh số lại.
// 3 (Blocked), 4 (NeedsReview) và 8 (Canceled) từng được khai báo nhưng chưa bao giờ có đường nào đặt —
// hủy là việc của WorkflowRun (WorkflowRunStatus.Canceled), không phải của từng task.
public enum AgentTaskStatus
{
    Queued = 1,
    Running = 2,
    Completed = 5,
    Failed = 6,
    Retrying = 7
}
