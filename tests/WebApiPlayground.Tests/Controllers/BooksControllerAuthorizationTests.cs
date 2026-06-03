using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Api.Controllers;
using Xunit;

namespace WebApiPlayground.Tests.Controllers;

/// <summary>
/// Unit test che fissano l'<b>intento di sicurezza</b> di <see cref="BooksController"/> via reflection:
/// se un attributo di autorizzazione viene rimosso o cambia policy, questi test falliscono
/// senza dover avviare l'applicazione. Il comportamento HTTP reale (401/403/200) è coperto
/// dagli integration test.
/// </summary>
public class BooksControllerAuthorizationTests
{
    private static MethodInfo Endpoint(string name) =>
        typeof(BooksController).GetMethod(name)
        ?? throw new InvalidOperationException($"Metodo '{name}' non trovato su BooksController");

    [Fact]
    public void Controller_RequiresAuthentication()
    {
        var attribute = typeof(BooksController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
    }

    [Theory]
    [InlineData(nameof(BooksController.GetBooks))]
    [InlineData(nameof(BooksController.GetBookById))]
    public void ReadEndpoints_UseReadPolicy(string methodName)
    {
        var attribute = Endpoint(methodName).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(AuthorizationPolicies.ReadBooks, attribute!.Policy);
    }

    [Theory]
    [InlineData(nameof(BooksController.CreateBook))]
    [InlineData(nameof(BooksController.DeleteBook))]
    public void WriteEndpoints_UseWritePolicy(string methodName)
    {
        var attribute = Endpoint(methodName).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(AuthorizationPolicies.WriteBooks, attribute!.Policy);
    }
}
