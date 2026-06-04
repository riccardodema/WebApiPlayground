namespace WebApiPlayground.IntegrationTests.Infrastructure;

/// <summary>
/// Rotte versionate usate dai test, centralizzate (no magic string): la stessa risorsa <c>/books</c>
/// è raggiungibile sotto più versioni di API.
/// </summary>
public static class ApiTestRoutes
{
    public const string BooksV1 = "/api/v1/books";
    public const string BooksV2 = "/api/v2/books";
}
