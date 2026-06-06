# syntax=docker/dockerfile:1
#
# Immagine di runtime dell'API: multi-stage (SDK per build, runtime chiseled per l'esecuzione).
# È l'artefatto portabile e riproducibile dell'app — stessa immagine dev → CI → prod.
# NB: è cosa diversa da Testcontainers, che containerizza solo il DB *per i test*.
# Vedi .claude/context/docker.md.

# ─── build ──────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copia prima i soli file di progetto: il layer di `restore` resta in cache finché
# non cambia un .csproj (gli edit al solo codice non ripagano l'intero restore NuGet).
COPY src/WebApiPlayground.Api/WebApiPlayground.Api.csproj src/WebApiPlayground.Api/
COPY src/WebApiPlayground.Application/WebApiPlayground.Application.csproj src/WebApiPlayground.Application/
COPY src/WebApiPlayground.Domain/WebApiPlayground.Domain.csproj src/WebApiPlayground.Domain/
COPY src/WebApiPlayground.Infrastructure/WebApiPlayground.Infrastructure.csproj src/WebApiPlayground.Infrastructure/
RUN dotnet restore src/WebApiPlayground.Api/WebApiPlayground.Api.csproj

# Copia il resto e pubblica (framework-dependent: il runtime chiseled porta già il framework).
COPY src/ src/
RUN dotnet publish src/WebApiPlayground.Api/WebApiPlayground.Api.csproj \
    -c Release -o /app --no-restore

# ─── runtime ──────────────────────────────────────────────────────────────────
# Chiseled = distroless-style: niente shell né package manager, gira come utente
# NON-root 'app' (UID 64198). Superficie d'attacco minima, pull più veloci.
# (Per questo NON c'è un HEALTHCHECK basato su curl: le probe sono HTTP esterne —
# compose/orchestratore — vedi .claude/lessons.md [L23].)
# Variante "-extra": include ICU + tzdata. Serve perché Microsoft.Data.SqlClient (EF Core
# SqlServer) NON supporta la Globalization Invariant Mode: con la chiseled "liscia" (senza ICU)
# l'app parte ma ogni query DB esplode con "Globalization Invariant Mode is not supported". [L23]
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app

# Porta non privilegiata (un utente non-root non può fare bind sotto la 1024).
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app .

# Esplicita l'utente non-root (oltre al default dell'immagine chiseled).
USER $APP_UID

ENTRYPOINT ["dotnet", "WebApiPlayground.Api.dll"]
