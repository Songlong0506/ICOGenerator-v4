using System.ComponentModel;
using System.Reflection;

namespace ICOGenerator.Domain.Enums;

public static class UserRoleExtensions
{
    /// <summary>Tên hiển thị của role lấy từ [Description], fallback về tên enum.</summary>
    public static string GetTitle(this UserRole role)
    {
        var member = typeof(UserRole).GetMember(role.ToString()).FirstOrDefault();
        var description = member?.GetCustomAttribute<DescriptionAttribute>()?.Description;
        return string.IsNullOrWhiteSpace(description) ? role.ToString() : description;
    }
}
