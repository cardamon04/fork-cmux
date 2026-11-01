using Cmux.Desktop;

// A private copy of this test executable supplies bounded synthetic Git stdout.
// The normal application's environment and executable search are untouched.
internal static class GitOutputFixture
{
    internal static int? RunProcessMode(string[] args)
    {
        if (Environment.GetEnvironmentVariable("CMUX_TEST_GIT_OUTPUT") != "1") return null;
        if (Path.GetFileName(Environment.ProcessPath).Equals("git.exe", StringComparison.OrdinalIgnoreCase))
        {
            Console.Write("refs/heads/" + new string('x', 33000));
            return 0;
        }
        if (args is ["--git-read-output-fixture", var directory])
        {
            var result = GitStatus.Read(directory, CancellationToken.None).GetAwaiter().GetResult();
            if (result is not null) throw new InvalidOperationException("The metadata reader accepted oversized stdout.");
            Console.WriteLine("owned-reader-rejected-output");
            return 0;
        }
        return null;
    }
}
