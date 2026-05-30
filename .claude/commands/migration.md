# /migration — EF Core Migrations

**Uso:** `/migration <NomeMigration>` (es. `/migration AddPublisherEntity`)

## Prerequisiti

1. `dotnet-ef` installato: `dotnet tool install --global dotnet-ef`
2. Connection string valida in `appsettings.Development.json` (vedere `.claude/context/stack.md`)

## Comandi

```bash
# Aggiungere
dotnet ef migrations add <Nome> \
  --project src/WebApiPlayground.Infrastructure \
  --startup-project src/WebApiPlayground.Api

# Applicare
dotnet ef database update \
  --project src/WebApiPlayground.Infrastructure \
  --startup-project src/WebApiPlayground.Api

# Rimuovere ultima (solo se NON applicata al DB)
dotnet ef migrations remove \
  --project src/WebApiPlayground.Infrastructure \
  --startup-project src/WebApiPlayground.Api
```

## Naming convention

| Caso | Nome |
|------|------|
| Prima migration | `InitialCreate` |
| Nuova entità | `Add<Entity>Entity` |
| Nuovo campo | `Add<Field>To<Entity>` |
| Rimozione campo | `Remove<Field>From<Entity>` |

## Troubleshooting

| Errore | Soluzione |
|--------|-----------|
| `No DbContext was found` | Verificare path `--project` e `--startup-project` |
| `Connection refused` | Verificare connection string e SQL Server attivo |
| `Unable to create PlaygroundDbContext` | Aggiungere `IDesignTimeDbContextFactory` o connection string in `appsettings` |
| Migration già applicata | Creare una nuova migration, non rieseguire |
