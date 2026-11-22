using ICOGenerator.Services.Organization;
using Xunit;

namespace ICOGenerator.Tests.Organization;

// Lịch đồng bộ tính theo giờ Việt Nam trên cả máy Windows (ID Windows) lẫn Linux (ID IANA).
public class OrganizationSyncJobTests
{
    [Theory]
    [InlineData("Asia/Ho_Chi_Minh")]
    [InlineData("SE Asia Standard Time")]
    public void ResolveTimeZone_AcceptsIanaAndWindowsIds(string id)
    {
        var zone = OrganizationSyncJob.ResolveTimeZone(id);

        Assert.Equal(TimeSpan.FromHours(7), zone.BaseUtcOffset);
    }

    [Fact]
    public void ResolveTimeZone_UnknownId_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => OrganizationSyncJob.ResolveTimeZone("Mars/Olympus"));
    }
}
