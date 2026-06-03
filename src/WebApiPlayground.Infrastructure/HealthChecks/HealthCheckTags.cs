namespace WebApiPlayground.Infrastructure.HealthChecks;

/// <summary>
/// Tag per classificare gli health check. Un check marcato <see cref="Ready"/> verifica una
/// dipendenza esterna (es. il database): entra nel probe di <c>readiness</c> ("posso servire
/// traffico?"), <b>non</b> in quello di <c>liveness</c> ("il processo è vivo?"). Un check di
/// dipendenza nel liveness causerebbe restart inutili quando la dipendenza è temporaneamente giù.
/// </summary>
public static class HealthCheckTags
{
    public const string Ready = "ready";
}
