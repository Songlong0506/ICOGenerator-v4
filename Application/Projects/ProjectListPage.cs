namespace ICOGenerator.Application.Projects;

public record ProjectListPage(
    IReadOnlyList<ProjectListItem> Items,
    int Page,
    int PageSize,
    int TotalCount,
    // Danh sách đơn vị cho dropdown "Đơn vị yêu cầu" trong modal New Project (department xếp trước).
    // Trang Index chứa luôn modal tạo mới nên page query trả kèm — controller không phải gọi query thứ hai.
    IReadOnlyList<OrgUnitOption> OrgUnitOptions)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
    public int FirstItemIndex => TotalCount == 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int LastItemIndex => Math.Min(Page * PageSize, TotalCount);
}
