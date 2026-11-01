namespace Pve.Term.Tests;

internal static class RepositoryPaths
{
    // Platform-specific builds add x64/ARM64 below bin; do not assume a fixed depth.
    internal static string Root { get; } = FindRoot();

    static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SpiceViewer.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Cannot find the test repository root.");
    }
}
