using ArchForge.Analysis.Core;

namespace ArchForge.Analysis.Tests;

public class PathFilterTests
{
    [Theory]
    [InlineData("/repo", "/repo/.claude/worktrees/wt/src/A.cs", true)] // копия git-worktree
    [InlineData("/repo", "/repo/.git/x.cs", true)]                     // .git
    [InlineData("/repo", "/repo/.vs/x.cs", true)]                      // .vs
    [InlineData("/repo", "/repo/backup/A.cs", true)]                   // backup (без точки в имени)
    [InlineData("/repo", "/repo/node_modules/a/x.cs", true)]          // node_modules
    [InlineData("/repo", "/repo/src/Auth.API/Foo.cs", false)]         // точка в ИМЕНИ папки — не скрытая
    [InlineData("/repo", "/repo/src/Kalmykov.ApiGateway/Program.cs", false)]
    public void IsHiddenOrCopy_detects_copies_and_hidden(string root, string path, bool expected)
        => Assert.Equal(expected, PathFilter.IsHiddenOrCopy(root, path));

    [Fact]
    public void Bin_is_kept_by_IsHiddenOrCopy_but_dropped_by_IsNoise()
    {
        // reference-сборкам bin/ нужен → IsHiddenOrCopy его НЕ трогает; исходникам — IsNoise отсекает
        Assert.False(PathFilter.IsHiddenOrCopy("/repo", "/repo/src/App/bin/Debug/App.dll"));
        Assert.True(PathFilter.IsNoise("/repo", "/repo/src/App/bin/Debug/App.dll"));
        // но bin ВНУТРИ копии под .claude — отсекается обеими
        Assert.True(PathFilter.IsHiddenOrCopy("/repo", "/repo/.claude/worktrees/wt/App/bin/App.dll"));
    }

    [Fact]
    public void Root_under_hidden_folder_does_not_filter_its_own_files()
    {
        // корень таргета сам под .config — сам таргет не отсекаем (проверка относительно корня)
        Assert.False(PathFilter.IsNoise("/home/.config/proj", "/home/.config/proj/src/Foo.cs"));
        // но копия ВНУТРИ такого таргета — по-прежнему отсекается
        Assert.True(PathFilter.IsNoise("/home/.config/proj", "/home/.config/proj/.claude/wt/Foo.cs"));
    }
}
