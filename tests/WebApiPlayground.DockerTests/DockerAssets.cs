namespace WebApiPlayground.DockerTests;

/// <summary>
/// Localizza la root del repo (la cartella con <c>WebApiPlayground.sln</c>) risalendo da
/// <see cref="AppContext.BaseDirectory"/>, e legge gli artefatti Docker as-code per le asserzioni.
/// Stesso approccio di <c>BicepArm</c> in IacTests.
/// </summary>
internal static class DockerAssets
{
    public static string RepoRoot { get; } = ResolveRepoRoot();

    public static bool Exists(string relativePath) => File.Exists(Path.Combine(RepoRoot, relativePath));

    public static string Read(string relativePath)
    {
        var path = Path.Combine(RepoRoot, relativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Artefatto Docker atteso a '{relativePath}' (relativo alla repo root).", path);
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WebApiPlayground.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Repo root (WebApiPlayground.sln) non trovata risalendo da {AppContext.BaseDirectory}.");
    }
}
