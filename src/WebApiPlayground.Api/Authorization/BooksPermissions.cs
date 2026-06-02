namespace WebApiPlayground.Api.Authorization;

/// <summary>
/// Nomi dei permessi Entra ID per la risorsa Books.
/// <list type="bullet">
/// <item><b>Scope</b> (claim <c>scp</c>): permessi delegati, flusso utente→API.</item>
/// <item><b>AppPermission</b> (claim <c>roles</c>): app permission, flusso macchina→macchina.</item>
/// </list>
/// </summary>
public static class BooksPermissions
{
    // Delegated scopes (claim "scp")
    public const string ScopeRead = "Books.Read";
    public const string ScopeReadWrite = "Books.ReadWrite";

    // Application permissions / app roles (claim "roles")
    public const string AppRead = "Books.Read.All";
    public const string AppReadWrite = "Books.ReadWrite.All";

    /// <summary>Scope che concedono la lettura (lettura o scrittura).</summary>
    public static readonly string[] ReadScopes = [ScopeRead, ScopeReadWrite];

    /// <summary>App permission che concedono la lettura (lettura o scrittura).</summary>
    public static readonly string[] ReadAppPermissions = [AppRead, AppReadWrite];

    /// <summary>Scope che concedono la scrittura.</summary>
    public static readonly string[] WriteScopes = [ScopeReadWrite];

    /// <summary>App permission che concedono la scrittura.</summary>
    public static readonly string[] WriteAppPermissions = [AppReadWrite];
}
