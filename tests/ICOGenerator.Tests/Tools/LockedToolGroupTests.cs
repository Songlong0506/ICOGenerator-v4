using System.Reflection;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Tools.Registry;
using Xunit;

namespace ICOGenerator.Tests.Tools;

// Nhóm cấp-cả-gói được khai bằng attribute trên class *Tools, và cả UI lẫn UpdateAgentUseCase đều đọc
// qua ToolDiscoveryService.AllOrNothingGroups. Bộ test này giữ sợi dây đó: khoá nhóm phải đúng bằng
// ToolDefinition.ServiceType (= tên class), nếu không thì màn hình Agents tra không ra và nhóm lặng lẽ
// quay về mười ô tick lẻ.
public class LockedToolGroupTests
{
    [Fact]
    public void WebTools_IsDeclaredAllOrNothing()
    {
        Assert.Contains(ToolDiscoveryService.AllOrNothingGroups, g => g.ServiceType == nameof(WebTools));
    }

    [Fact]
    public void EveryLockedGroup_KeysOnAToolTypeName()
    {
        var toolTypeNames = ToolDiscoveryService.ToolTypes.Select(t => t.Name).ToHashSet();

        foreach (var group in ToolDiscoveryService.AllOrNothingGroups)
            Assert.Contains(group.ServiceType, toolTypeNames);
    }

    [Fact]
    public void EveryLockedGroup_HasALabelAndDescriptionForTheAgentsScreen()
    {
        foreach (var group in ToolDiscoveryService.AllOrNothingGroups)
        {
            Assert.False(string.IsNullOrWhiteSpace(group.DisplayName), $"{group.ServiceType}: thiếu nhãn nhóm.");
            Assert.False(string.IsNullOrWhiteSpace(group.Description), $"{group.ServiceType}: thiếu mô tả nhóm.");
        }
    }

    [Fact]
    public void ALockedGroup_HasMoreThanOneTool()
    {
        // Nhóm một tool mà khai cấp-cả-gói là khai thừa — và màn hình sẽ vẽ một lớp bọc quanh đúng một dòng.
        foreach (var group in ToolDiscoveryService.AllOrNothingGroups)
        {
            var count = ToolDiscoveryService.ToolTypes
                .Single(t => t.Name == group.ServiceType)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Count(m => m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() != null);

            Assert.True(count > 1, $"{group.ServiceType} chỉ có {count} tool — không cần khoá nhóm.");
        }
    }
}
