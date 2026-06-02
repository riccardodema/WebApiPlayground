using System.Diagnostics;
using System.Text.Json;

namespace WebApiPlayground.IacTests;

/// <summary>
/// Compila un file Bicep in ARM (JSON) e ne espone la radice per le asserzioni.
/// Nessun Azure richiesto: usa la Bicep CLI locale (<c>bicep</c> o <c>az bicep</c>).
/// I risultati sono cache-ati per non ricompilare a ogni test.
/// </summary>
public static class BicepArm
{
    private static readonly string InfraDir = ResolveInfraDir();
    private static readonly string RepoRoot = Directory.GetParent(InfraDir)!.FullName;
    private static readonly Dictionary<string, JsonDocument> Cache = new();
    private static readonly Lock Gate = new();

    /// <summary>True se una Bicep CLI è disponibile: i test si SKIPpano quando è false.</summary>
    public static bool Available { get; } = FindBicep() is not null;

    /// <summary>Compila un template Bicep (path relativo a <c>infra/</c>) e ne restituisce la radice ARM.</summary>
    public static JsonElement Compile(string relativePath)
    {
        lock (Gate)
        {
            if (!Cache.TryGetValue(relativePath, out var doc))
            {
                doc = JsonDocument.Parse(Build(relativePath));
                Cache[relativePath] = doc;
            }
            return doc.RootElement;
        }
    }

    /// <summary>Risorse di un dato tipo nel template ARM.</summary>
    public static IEnumerable<JsonElement> Resources(JsonElement template, string resourceType) =>
        template.GetProperty("resources").EnumerateArray()
            .Where(r => r.GetProperty("type").GetString() == resourceType);

    private static string Build(string relativePath)
    {
        var full = Path.Combine(InfraDir, relativePath);
        var bicep = FindBicep() ?? throw new InvalidOperationException("Bicep CLI non trovata.");

        var psi = new ProcessStartInfo
        {
            FileName = bicep,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // `bicep build <file> --stdout` vs `az bicep build --file <file> --stdout`
        if (Path.GetFileNameWithoutExtension(bicep).Equals("az", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("bicep");
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add("--file");
            psi.ArgumentList.Add(full);
            psi.ArgumentList.Add("--stdout");
        }
        else
        {
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add(full);
            psi.ArgumentList.Add("--stdout");
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Impossibile avviare '{bicep}'.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Compilazione Bicep fallita per '{relativePath}':\n{stderr}");

        return stdout;
    }

    private static string? FindBicep()
    {
        var exe = OperatingSystem.IsWindows() ? "bicep.exe" : "bicep";

        // 1. Override esplicito (qualunque binario bicep).
        var configured = Environment.GetEnvironmentVariable("BICEP_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        // 2. Binario locale del repo, scaricato da `infra/tests/install-bicep.sh`.
        var local = Path.Combine(RepoRoot, ".tools", exe);
        if (File.Exists(local)) return local;

        // 3. Posizione standard di `az bicep install`.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var azBicep = Path.Combine(home, ".azure", "bin", exe);
            if (File.Exists(azBicep)) return azBicep;
        }

        // 4. PATH: bicep standalone, poi l'Azure CLI (`az bicep`).
        return ExecutableOnPath("bicep") ?? ExecutableOnPath("az");
    }

    private static string? ExecutableOnPath(string name)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { name + ".exe", name + ".cmd", name + ".bat" }
            : new[] { name };

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(dir, candidate);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    private static string ResolveInfraDir()
    {
        // I test girano da bin/<cfg>/net10.0: risali finché trovi infra/main.bicep.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "infra", "main.bicep")))
                return Path.Combine(dir.FullName, "infra");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Cartella 'infra/' non trovata risalendo da {AppContext.BaseDirectory}.");
    }
}
