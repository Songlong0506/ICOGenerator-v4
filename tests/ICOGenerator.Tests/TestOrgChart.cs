using ICOGenerator.Data;
using ICOGenerator.Services.Organization;
using Microsoft.Extensions.Caching.Memory;

namespace ICOGenerator.Tests;

/// <summary>
/// Cây orgUnit cho test: đọc từ chính DB của test, cache riêng mỗi lần dựng nên seed mới luôn thấy được
/// (khác DI thật, nơi một IMemoryCache dùng chung cả tiến trình). Phần lớn test không seed OrgUnits ⇒ cây
/// rỗng ⇒ mọi dự án rơi về bucket chung, đúng hành vi fail-open cần khẳng định.
/// </summary>
public static class TestOrgChart
{
    public static OrgChartProvider NewProvider(AppDbContext db) =>
        new(db, new MemoryCache(new MemoryCacheOptions()));
}
