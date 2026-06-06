# syntax=docker/dockerfile:1
#
# Servizio "migrations" one-shot per docker-compose: builda il DACPAC e lo pubblica
# (schema + seed) sullo SQL Server del compose, RIUSANDO lo stesso deploy.sh usato in
# locale e in CI/CD — fedele a "DACPAC = source of truth". Legge DB_CONNECTION da env.
# Esce a 0 a fine publish → l'API parte con `service_completed_successfully`.
# Vedi .claude/context/docker.md e database/README.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /db

# deploy.sh richiede `sqlpackage` sul PATH: installalo come global tool.
RUN dotnet tool install -g microsoft.sqlpackage
ENV PATH="${PATH}:/root/.dotnet/tools"

# Copia il progetto database (bin/obj esclusi da database/.dockerignore) e pre-builda
# il DACPAC: così `up` paga solo la publish (deploy.sh rifà un build incrementale).
COPY . .
RUN dotnet build WebApiPlayground.Database.sqlproj -c Release -v minimal

# deploy.sh: build (incrementale) + `sqlpackage /Action:Publish`. DB_CONNECTION da compose.
ENTRYPOINT ["./deploy.sh"]
