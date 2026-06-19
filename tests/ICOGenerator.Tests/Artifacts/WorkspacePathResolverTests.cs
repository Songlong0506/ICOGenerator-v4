using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

public class WorkspacePathResolverTests
{
    [Fact]
    public void GetWorkspaceFolder_DistinctIds_ProduceDistinctFolders_EvenWhenNamesNormaliseEqually()
    {
        // "Task App" and "task-app" both normalise to the same safe folder name, so before
        // keying by Id they shared a workspace and overwrote each other's artifacts.
        var a = WorkspacePathResolver.GetWorkspaceFolder(Guid.NewGuid(), "Task App");
        var b = WorkspacePathResolver.GetWorkspaceFolder(Guid.NewGuid(), "task-app");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GetWorkspaceFolder_IsStableForSameProject()
    {
        var id = Guid.NewGuid();

        var first = WorkspacePathResolver.GetWorkspaceFolder(id, "My Project");
        var second = WorkspacePathResolver.GetWorkspaceFolder(id, "My Project");

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetWorkspaceFolder_PrefixesSafeName_AndAppendsShortId()
    {
        var folder = WorkspacePathResolver.GetWorkspaceFolder(Guid.NewGuid(), "Task App");

        Assert.StartsWith("task-app-", folder);
        Assert.Equal("task-app-".Length + 8, folder.Length);
    }
}
