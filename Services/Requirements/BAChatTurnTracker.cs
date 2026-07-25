using System.Collections.Concurrent;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Ghi nhận các project đang có MỘT lượt trả lời của BA chạy dở TRONG TIẾN TRÌNH NÀY (ChatStream chạy
/// lượt chat với CancellationToken.None nên nó vẫn chạy tiếp dù client đã đóng tab).
/// <para>
/// Cần thiết vì "lượt hội thoại mới nhất là của user" một mình KHÔNG phân biệt được hai tình huống
/// trái ngược: (a) BA đang soạn thật — chờ thêm là đúng; (b) lượt trả lời đã CHẾT (tiến trình khởi động
/// lại giữa chừng, lỗi hạ tầng nuốt mất lượt assistant) — chờ tiếp là treo màn hình vĩnh viễn ở
/// "BA đang soạn câu trả lời…", F5 cũng không thoát. Có sổ theo dõi này, endpoint ChatReplyStatus trả
/// thêm cờ <c>stale</c> để UI mở khóa ô nhập và mời "Thử lại" thay vì quay mãi.
/// </para>
/// Singleton, chỉ nằm trong bộ nhớ tiến trình: chạy nhiều instance thì instance khác không thấy lượt
/// đang chạy ở đây — nên bên đọc còn kèm điều kiện "lượt user đã cũ hơn ngưỡng ân hạn"
/// (<see cref="BAChatService.ReplyStaleAfter"/>) trước khi kết luận là chết.
/// </summary>
public sealed class BAChatTurnTracker
{
    // Đếm (không phải cờ bool) vì hai tab có thể cùng chạy lượt cho một project — lượt sau kết thúc
    // không được xóa dấu của lượt trước.
    private readonly ConcurrentDictionary<Guid, int> _running = new();

    /// <summary>Đánh dấu project đang chạy một lượt BA; Dispose (khi lượt kết thúc dù thành công hay lỗi) gỡ dấu.</summary>
    public IDisposable Begin(Guid projectId)
    {
        _running.AddOrUpdate(projectId, 1, (_, count) => count + 1);
        return new Registration(this, projectId);
    }

    /// <summary>
    /// Như <see cref="Begin"/> nhưng CHỈ khi project chưa có lượt nào đang chạy — kiểm tra và ghi dấu là
    /// một thao tác nguyên tử, nên hai request cùng lúc không thể cùng thắng. Dùng cho "Thử lại": chạy
    /// lại một lượt đang được trả lời ở nơi khác sẽ tạo hai câu trả lời cho cùng một câu hỏi.
    /// </summary>
    public bool TryBeginExclusive(Guid projectId, out IDisposable? registration)
    {
        if (!_running.TryAdd(projectId, 1))
        {
            registration = null;
            return false;
        }

        registration = new Registration(this, projectId);
        return true;
    }

    /// <summary>Project có lượt BA nào đang chạy trong tiến trình này không.</summary>
    public bool IsRunning(Guid projectId) => _running.ContainsKey(projectId);

    private void End(Guid projectId)
    {
        while (_running.TryGetValue(projectId, out var count))
        {
            if (count <= 1)
            {
                if (((ICollection<KeyValuePair<Guid, int>>)_running).Remove(new KeyValuePair<Guid, int>(projectId, count)))
                    return;
            }
            else if (_running.TryUpdate(projectId, count - 1, count))
            {
                return;
            }
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly BAChatTurnTracker _tracker;
        private readonly Guid _projectId;
        private bool _disposed;

        public Registration(BAChatTurnTracker tracker, Guid projectId)
        {
            _tracker = tracker;
            _projectId = projectId;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _tracker.End(_projectId);
        }
    }
}
