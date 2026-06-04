namespace WebApiPlayground.Api.Versioning;

/// <summary>
/// Versioni dell'API esposte, centralizzate (no magic number): i <c>const double</c> sono costanti
/// compile-time usabili negli attributi <c>[ApiVersion]</c>/<c>[MapToApiVersion]</c>. <see cref="All"/>
/// è l'unica fonte di verità su quali versioni esistono — da cui si registra un documento OpenAPI e
/// una voce Scalar per versione. Vedi <c>.claude/context/api-versioning.md</c>.
/// </summary>
public static class ApiVersions
{
    public const double V1 = 1.0;
    public const double V2 = 2.0;

    /// <summary>Le versioni esposte, in ordine crescente (l'ultima è la default in Scalar).</summary>
    public static readonly IReadOnlyList<double> All = [V1, V2];

    /// <summary>
    /// Nome del gruppo/documento per una versione, nel formato di
    /// <c>ApiExplorer.GroupNameFormat = "'v'VVV"</c> (es. 1.0 → <c>"v1"</c>). Deve restare allineato a
    /// quel format: è il nome con cui il documento OpenAPI nativo filtra le operazioni della versione.
    /// </summary>
    public static string GroupName(double version) => FormattableString.Invariant($"v{(int)version}");
}
