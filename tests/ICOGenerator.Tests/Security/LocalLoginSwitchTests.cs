using ICOGenerator.Configuration;
using ICOGenerator.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ICOGenerator.Tests.Security;

// IdentityServer:LocalRole là công tắc chọn kiểu đăng nhập. Chốt chặn cần giữ: thiếu key hoặc để trống phải
// rơi về SSO (không bao giờ về "ai cũng là SuperAdmin"), còn gõ sai tên vai trò phải làm hỏng việc bind (app
// dừng lúc khởi động) thay vì lặng lẽ đổi chế độ.
public class LocalLoginSwitchTests
{
    private static IdentityServerSettings Bind(string? localRole)
    {
        var values = new Dictionary<string, string?> { ["IdentityServer:BaseURL"] = "https://idp.example/" };
        if (localRole is not null)
            values["IdentityServer:LocalRole"] = localRole;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            .GetSection(IdentityServerSettings.SectionName).Get<IdentityServerSettings>()!;
    }

    [Fact]
    public void MissingKey_MeansSso()
    {
        var settings = Bind(null);
        Assert.Null(settings.LocalRole);
        Assert.False(settings.IsLocalLogin);
    }

    [Fact]
    public void EmptyString_MeansSso()
    {
        var settings = Bind("");
        Assert.Null(settings.LocalRole);
        Assert.False(settings.IsLocalLogin);
    }

    [Theory]
    [InlineData("SuperAdmin", UserRole.SuperAdmin)]
    [InlineData("teamdev", UserRole.TeamDev)]
    public void RoleName_MeansLocalLogin(string raw, UserRole expected)
    {
        var settings = Bind(raw);
        Assert.True(settings.IsLocalLogin);
        Assert.Equal(expected, settings.LocalRole);
    }

    [Fact]
    public void Typo_FailsBinding()
    {
        Assert.Throws<InvalidOperationException>(() => Bind("Admn"));
    }

    [Fact]
    public void DefaultInstance_IsSso()
    {
        Assert.False(new IdentityServerSettings().IsLocalLogin);
    }
}
