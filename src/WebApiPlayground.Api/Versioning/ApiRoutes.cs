namespace WebApiPlayground.Api.Versioning;

/// <summary>
/// Template di rotta versionati, centralizzati (no magic string): la versione è un segmento dell'URL
/// (<c>/api/v{version}/...</c>), schema esplicito e visibile in Scalar. La rotta della risorsa è
/// condivisa così v1 e v2 (due controller) puntano alla <b>stessa</b> URL <c>/books</c>, distinti solo
/// dalla versione. Vedi <c>.claude/context/api-versioning.md</c>.
/// </summary>
public static class ApiRoutes
{
    /// <summary>Prefisso versionato per segmento URL (es. <c>/api/v1</c>).</summary>
    private const string VersionPrefix = "api/v{version:apiVersion}";

    /// <summary>
    /// Rotta della risorsa Books, usata da <c>BooksController</c> (v1) e <c>BooksV2Controller</c> (v2):
    /// entrambi mappano su <c>/api/v{version}/books</c>, così la stessa URL serve più versioni.
    /// </summary>
    public const string Books = VersionPrefix + "/books";
}
