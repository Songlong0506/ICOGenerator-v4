using ICOGenerator.Localization;
using System.ComponentModel;
using System.Reflection;

namespace ICOGenerator.Domain.Enums;

public static class EnumDisplay
{
    /// <summary>
    /// Tên hiển thị của một enum value lấy từ [Description], fallback về tên enum. Một chỗ duy nhất
    /// cho việc tra [Description] (trước đây lặp ở UserRoleExtensions.GetTitle, AgentRoleKeyExtensions.GetTitle
    /// và PermissionCatalog.GetLabel).
    /// </summary>
    public static string GetTitle<TEnum>(this TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();

        // Chữ người dùng đọc nằm ở resource `Enum.<Kiểu>.<Giá trị>`, mỗi ngôn ngữ một bản. Enum chưa có
        // resource (nhãn kỹ thuật, không dịch) thì rơi về [Description] như trước.
        var key = $"Enum.{typeof(TEnum).Name}.{name}";
        var localized = Loc.T(key);
        if (localized != key)
            return localized;

        var member = typeof(TEnum).GetMember(name).FirstOrDefault();
        var description = member?.GetCustomAttribute<DescriptionAttribute>()?.Description;
        return string.IsNullOrWhiteSpace(description) ? name : description;
    }
}
