using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using WebApiPlayground.IntegrationTests.Infrastructure;

namespace WebApiPlayground.IntegrationTests.Database;

/// <summary>
/// Factory che fa girare l'app contro lo schema deployato dal <b>DACPAC</b> (DacFx programmatico,
/// stesso pacchetto che pubblicano compose/CI/`deploy.sh`) invece che da <c>EnsureCreated</c>.
/// Chiude il buco "i test girano su uno schema che nessuno deploya": il resto della suite usa lo
/// schema generato dal modello EF, che può divergere in silenzio da quello vero — qui il drift
/// diventa un test rosso (vedi <c>DacpacSchemaParityTests</c> e <c>.claude/lessons.md</c> [L27]).
/// Il deploy include il post-deployment script → catalogo seedato (100 libri / 45 autori).
/// </summary>
public sealed class DacpacDeployedApiFactory : PlaygroundApiFactory
{
    public const string DatabaseName = "PlaygroundDatabase";

    /// <summary>L'app punta al database deployato dal DACPAC, non al default del container.</summary>
    protected override string AppConnectionString =>
        new SqlConnectionStringBuilder(SqlConnectionString) { InitialCatalog = DatabaseName }.ConnectionString;

    protected override Task OnSqlContainerStartedAsync()
    {
        // Lo stesso publish di deploy.sh/compose, via API DacFx (niente sqlpackage da installare):
        // crea il database, applica lo schema e il post-deployment seed. Le opzioni rispecchiano
        // il publish profile (BlockOnPossibleDataLoss, niente drop di oggetti fuori source).
        var services = new DacServices(SqlConnectionString);
        using var package = DacPackage.Load(FindDacpacPath());

        services.Deploy(package, DatabaseName, upgradeExisting: true, new DacDeployOptions
        {
            BlockOnPossibleDataLoss = true,
            DropObjectsNotInSource = false,
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Il DACPAC è prodotto dalla build della SOLUTION (il .sqlproj ne fa parte): si risale alla
    /// radice del repo e si prende il più recente tra Debug e Release. Se manca si fallisce con
    /// l'istruzione esatta — un test di parità che si salta in silenzio non protegge nessuno.
    /// </summary>
    private static string FindDacpacPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WebApiPlayground.sln")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Radice del repository non trovata (WebApiPlayground.sln).");

        var candidates = new[] { "Release", "Debug" }
            .Select(c => Path.Combine(dir.FullName, "database", "bin", c, "WebApiPlayground.Database.dacpac"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        return candidates.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "DACPAC non trovato in database/bin/{Debug|Release}. Builda la solution prima dei test: " +
                "'dotnet build WebApiPlayground.sln' (il progetto SQL produce il pacchetto).");
    }
}
