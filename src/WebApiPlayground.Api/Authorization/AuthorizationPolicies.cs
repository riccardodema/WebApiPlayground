namespace WebApiPlayground.Api.Authorization;

/// <summary>Nomi delle authorization policy applicate agli endpoint.</summary>
public static class AuthorizationPolicies
{
    /// <summary>Lettura books: scope/app-permission di lettura o scrittura.</summary>
    public const string ReadBooks = "ReadBooks";

    /// <summary>Scrittura books: scope/app-permission di scrittura.</summary>
    public const string WriteBooks = "WriteBooks";
}
