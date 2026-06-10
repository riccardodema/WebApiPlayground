using Xunit;

namespace WebApiPlayground.DockerTests;

/// <summary>
/// Contract test statici: asseriscono che gli artefatti Docker rispettino le best practice
/// documentate (chiseled non-root, nessun segreto in chiaro, porta 8080, healthcheck, ordine
/// migrations→api, ...). Veloci e senza Docker. Vedi .claude/context/docker.md.
/// </summary>
public class DockerArtifactsContractTests
{
    // ----- Dockerfile (API) ---------------------------------------------------

    [Fact]
    public void Dockerfile_exists() => Assert.True(DockerAssets.Exists("Dockerfile"));

    [Fact]
    public void Dockerfile_is_multi_stage()
    {
        var dockerfile = DockerAssets.Read("Dockerfile");
        Assert.Contains("AS build", dockerfile);
        Assert.Contains("AS final", dockerfile);
    }

    [Fact]
    public void Dockerfile_uses_dotnet10_sdk_and_chiseled_extra_runtime()
    {
        var dockerfile = DockerAssets.Read("Dockerfile");
        Assert.Contains("mcr.microsoft.com/dotnet/sdk:10.0", dockerfile);
        // "-extra": chiseled CON ICU. Microsoft.Data.SqlClient (EF Core) NON supporta la Globalization
        // Invariant Mode → con la chiseled liscia ogni query DB fallirebbe a runtime. Vedi [L23].
        Assert.Contains("mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra", dockerfile);
    }

    [Fact]
    public void Dockerfile_runs_as_non_root_user()
    {
        var dockerfile = DockerAssets.Read("Dockerfile");
        // Una direttiva USER esplicita ($APP_UID o app): mai root a runtime.
        Assert.Matches(@"(?im)^\s*USER\s+(\$?APP_UID|app)\b", dockerfile);
    }

    [Fact]
    public void Dockerfile_uses_non_privileged_port_8080()
    {
        var dockerfile = DockerAssets.Read("Dockerfile");
        Assert.Contains("ASPNETCORE_HTTP_PORTS", dockerfile);
        Assert.Contains("8080", dockerfile);
    }

    [Fact]
    public void Dockerfile_entrypoint_runs_the_api()
        => Assert.Contains("WebApiPlayground.Api.dll", DockerAssets.Read("Dockerfile"));

    // ----- .dockerignore ------------------------------------------------------

    [Fact]
    public void Dockerignore_excludes_dev_secrets_and_build_output()
    {
        var dockerignore = DockerAssets.Read(".dockerignore");
        Assert.Contains("appsettings.Development.json", dockerignore);  // mai il segreto dev nell'immagine
        Assert.Contains("bin/", dockerignore);
        Assert.Contains("obj/", dockerignore);
        Assert.Contains(".env", dockerignore);
    }

    // ----- docker-compose.yml -------------------------------------------------

    [Fact]
    public void Compose_db_has_amd64_platform_and_healthcheck()
    {
        var compose = DockerAssets.Read("docker-compose.yml");
        Assert.Contains("mcr.microsoft.com/mssql/server", compose);
        Assert.Contains("platform: linux/amd64", compose);  // arm64 (Apple Silicon) → emulazione
        Assert.Contains("healthcheck:", compose);
    }

    [Fact]
    public void Compose_has_no_plaintext_sa_password()
    {
        var compose = DockerAssets.Read("docker-compose.yml");
        Assert.Contains("${MSSQL_SA_PASSWORD", compose);   // arriva da .env
        Assert.DoesNotContain("PrimeLewis", compose);      // la dev password reale non deve mai comparire
    }

    [Fact]
    public void Compose_api_waits_for_db_health_and_migrations_completion()
    {
        var compose = DockerAssets.Read("docker-compose.yml");
        Assert.Contains("service_healthy", compose);                 // attende il DB
        Assert.Contains("service_completed_successfully", compose);  // attende lo schema (migrations)
        Assert.Contains("8080:8080", compose);
    }

    [Fact]
    public void Compose_runs_service_bus_emulator_as_real_outbox_transport()
    {
        var compose = DockerAssets.Read("docker-compose.yml");
        // L'emulatore ASB è parte dello stack → l'outbox gira sul broker reale (publisher → coda → consumer).
        Assert.Contains("azure-messaging/servicebus-emulator", compose);
        // L'app vi punta via connection string statica dell'emulatore (host = nome servizio).
        Assert.Contains("ServiceBus__ConnectionString", compose);
        Assert.Contains("UseDevelopmentEmulator=true", compose);
    }

    [Fact]
    public void Service_bus_emulator_config_declares_the_outbox_queue()
    {
        var config = DockerAssets.Read("docker/servicebus-emulator/Config.json");
        // La coda dell'emulatore deve combaciare col default dell'app (ServiceBus:QueueName) e col Bicep.
        Assert.Contains("popularity-enrichment", config);
    }

    // ----- database/Dockerfile (migrations) -----------------------------------

    [Fact]
    public void Migrations_dockerfile_installs_sqlpackage_and_reuses_deploy_script()
    {
        var migrations = DockerAssets.Read("database/Dockerfile");
        Assert.Contains("microsoft.sqlpackage", migrations);
        Assert.Contains("deploy.sh", migrations);   // riusa lo script esistente (DACPAC = source of truth)
    }

    // ----- override opzionali (off di default) --------------------------------

    [Fact]
    public void Redis_override_wires_l2_cache()
    {
        var redis = DockerAssets.Read("docker-compose.redis.yml");
        Assert.Contains("redis", redis);
        Assert.Contains("Cache__Redis__ConnectionString", redis);
    }

    [Fact]
    public void Aspire_override_wires_otlp_endpoint()
    {
        var aspire = DockerAssets.Read("docker-compose.aspire.yml");
        Assert.Contains("aspire-dashboard", aspire);
        Assert.Contains("OpenTelemetry__OtlpEndpoint", aspire);
    }
}
