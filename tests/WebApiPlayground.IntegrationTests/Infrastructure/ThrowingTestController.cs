using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebApiPlayground.IntegrationTests.Infrastructure;

/// <summary>
/// Endpoint che esiste solo nei test: lancia un'eccezione non gestita per verificare che
/// <c>GlobalExceptionHandler</c> la trasformi in un ProblemDetails 500. Registrato nella
/// pipeline reale via <c>AddApplicationPart</c> in <see cref="PlaygroundApiFactory"/>.
/// <c>[ApiVersionNeutral]</c>: non partecipa al versioning (rotta senza segmento di versione),
/// così resta raggiungibile anche col versioning attivo.
/// </summary>
[ApiController]
[ApiVersionNeutral]
[Route("__tests__")]
[AllowAnonymous]
public sealed class ThrowingTestController : ControllerBase
{
    [HttpGet("throw")]
    public IActionResult Throw() =>
        throw new InvalidOperationException("Simulated unhandled failure for testing.");
}
