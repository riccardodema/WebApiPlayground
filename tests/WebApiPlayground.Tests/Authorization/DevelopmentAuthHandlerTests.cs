using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using WebApiPlayground.Api.Authorization;
using Xunit;

namespace WebApiPlayground.Tests.Authorization;

/// <summary>
/// Il bypass di sviluppo (<see cref="DevelopmentAuthHandler"/>) autentica OGNI richiesta come
/// <c>dev-user</c> con lo scope di scrittura — che soddisfa entrambe le policy (read e write).
/// Il gate che lo confina a Development è testato in <c>AuthenticationExtensionsTests</c>;
/// qui si verifica il contenuto del ticket che produce.
/// </summary>
public class DevelopmentAuthHandlerTests
{
    private static async Task<AuthenticateResult> AuthenticateAsync()
    {
        var options = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Setup(o => o.Get(DevelopmentAuthHandler.SchemeName)).Returns(new AuthenticationSchemeOptions());

        var handler = new DevelopmentAuthHandler(options.Object, NullLoggerFactory.Instance, UrlEncoder.Default);
        await handler.InitializeAsync(
            new AuthenticationScheme(DevelopmentAuthHandler.SchemeName, null, typeof(DevelopmentAuthHandler)),
            new DefaultHttpContext());

        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task Authenticates_every_request_as_dev_user()
    {
        var result = await AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("dev-user", result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task Grants_the_write_scope_which_satisfies_both_policies()
    {
        var result = await AuthenticateAsync();

        // scp = Books.ReadWrite → passa sia la policy di lettura sia quella di scrittura (vedi BooksPermissions).
        Assert.Equal(BooksPermissions.ScopeReadWrite, result.Principal!.FindFirstValue("scp"));
        Assert.Equal(DevelopmentAuthHandler.SchemeName, result.Ticket!.AuthenticationScheme);
    }
}
