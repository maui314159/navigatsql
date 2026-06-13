// Git HEAD resolution for the optional `gitSha` field on the kggraph envelope (#2).
//
// Pure metadata: every failure path degrades to null (never throws, never an
// error), so a scan of a non-git tree still emits a valid document. Determinism is
// preserved — the same tree resolves to the same SHA, with no timestamps or machine
// names entering the document. The caller resolves the SHA ONCE and hands it to
// KgGraphEmit.Build; the emitter never shells out per-file.

using System.Diagnostics;

internal static class Git
{
    /// HEAD SHA of the repository containing `path` (a file or directory), via
    /// `git -C <dir> rev-parse HEAD`. Returns null when `path` is outside a repo,
    /// git is absent, or the command fails for any reason.
    public static string? HeadSha(string path)
    {
        try
        {
            var dir = Directory.Exists(path)
                ? path
                : Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(dir)) return null;

            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(dir);
            psi.ArgumentList.Add("rev-parse"); psi.ArgumentList.Add("HEAD");

            using var p = Process.Start(psi);
            if (p is null) return null;
            var sha = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && sha.Length > 0 ? sha : null;
        }
        catch
        {
            // git missing, path vanished, permissions, etc. — all degrade to "no SHA".
            return null;
        }
    }

    /// The single SHA shared by every scan root, or null when the roots span more
    /// than one repository (or any root can't be resolved). Multi-root scans that
    /// don't share one HEAD omit `gitSha` rather than guess, so the staleness signal
    /// is never ambiguous about which tree it describes.
    public static string? CommonHeadSha(IEnumerable<string> roots)
    {
        string? common = null;
        var any = false;
        foreach (var root in roots)
        {
            var sha = HeadSha(root);
            if (sha is null) return null;
            if (!any) { common = sha; any = true; }
            else if (sha != common) return null;
        }
        return common;
    }
}
