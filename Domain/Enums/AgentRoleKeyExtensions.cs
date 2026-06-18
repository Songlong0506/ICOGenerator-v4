using System.ComponentModel;
using System.Reflection;

namespace ICOGenerator.Domain.Enums;

public static class AgentRoleKeyExtensions
{
    /// <summary>
    /// Role title from the [Description] attribute on the enum value. Replaces the old per-agent RoleTitle column.
    /// </summary>
    public static string GetTitle(this AgentRoleKey roleKey)
    {
        var member = typeof(AgentRoleKey).GetMember(roleKey.ToString()).FirstOrDefault();
        var description = member?.GetCustomAttribute<DescriptionAttribute>()?.Description;
        return string.IsNullOrWhiteSpace(description) ? roleKey.ToString() : description;
    }
}
