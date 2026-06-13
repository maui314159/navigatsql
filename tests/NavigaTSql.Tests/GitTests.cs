using System.Text.RegularExpressions;
using Xunit;

// Tests for Git HEAD resolution behind the kggraph envelope's optional gitSha (#2).
//
// The positive cases lean on the fact that the test binary runs from inside this
// repo's working tree (bin/Debug/...), so `git -C <basedir> rev-parse HEAD` walks
// up to the repo .git. The negative cases use a throwaway temp dir that has no .git
// ancestor — the documented "outside a git repo -> omit gitSha" path.
public class GitTests
{
    // A git object name: 40 hex (sha-1) or 64 hex (sha-256) lowercase.
    private static bool IsSha(string s) => Regex.IsMatch(s, "^[0-9a-f]{40}$|^[0-9a-f]{64}$");

    [Fact]
    public void HeadSha_InsideRepo_ReturnsObjectName()
    {
        var sha = Git.HeadSha(AppContext.BaseDirectory);
        Assert.NotNull(sha);
        Assert.True(IsSha(sha!), $"expected a git object name, got '{sha}'");
    }

    [Fact]
    public void HeadSha_OutsideGitRepo_ReturnsNull()
    {
        var tmp = Directory.CreateTempSubdirectory("navigatsql_nogit_");
        try
        {
            Assert.Null(Git.HeadSha(tmp.FullName));
        }
        finally
        {
            Directory.Delete(tmp.FullName, recursive: true);
        }
    }

    [Fact]
    public void HeadSha_NonexistentPath_ReturnsNullOrDoesNotThrow()
    {
        // Must never throw; result is null (or a parent repo's SHA, but no exception).
        var ex = Record.Exception(() => Git.HeadSha("/no/such/path/navigatsql_xyz"));
        Assert.Null(ex);
    }

    [Fact]
    public void CommonHeadSha_SingleRepoRoot_MatchesHeadSha()
    {
        var roots = new[] { AppContext.BaseDirectory };
        Assert.Equal(Git.HeadSha(AppContext.BaseDirectory), Git.CommonHeadSha(roots));
    }

    [Fact]
    public void CommonHeadSha_AnyRootOutsideRepo_ReturnsNull()
    {
        var tmp = Directory.CreateTempSubdirectory("navigatsql_nogit_");
        try
        {
            // One in-repo root + one non-repo root -> roots span repos -> omit.
            var roots = new[] { AppContext.BaseDirectory, tmp.FullName };
            Assert.Null(Git.CommonHeadSha(roots));
        }
        finally
        {
            Directory.Delete(tmp.FullName, recursive: true);
        }
    }

    [Fact]
    public void CommonHeadSha_EmptyRoots_ReturnsNull()
    {
        Assert.Null(Git.CommonHeadSha(Array.Empty<string>()));
    }
}
