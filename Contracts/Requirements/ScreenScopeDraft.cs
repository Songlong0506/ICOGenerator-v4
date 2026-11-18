namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả của lượt SOẠN PHẠM VI MÀN HÌNH (<c>ScreenScopeDraftService</c>): mỗi màn hình một
/// <see cref="ScreenScopeRow"/> — việc của màn, các chức năng, bước luồng mỗi chức năng phụ trách. Không đi
/// thẳng vào DB: <c>ScreenScopeMapBuilder.Build</c> kéo nó về đúng danh sách phạm vi cho phép rồi mới chốt.
/// </summary>
public class ScreenScopeDraft
{
    public List<ScreenScopeRow> Screens { get; set; } = new();
}
