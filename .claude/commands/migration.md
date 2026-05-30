# /migration — Gestione migrations EF Core

Guida per aggiungere, applicare e gestire le migrations Entity Framework Core nel progetto.

**Uso**: `/migration <NomeMigration>` (es. `/migration AddPublisherEntity`)

---

## Prerequisiti

### 1. Verificare la connection string

In `WebApiPlayground/Program.cs` la connection string è attualmente un placeholder:

```csharp
options.UseSqlServer("stringaacaso") // to be replaced
```

**Sostituire** con una connection string reale, preferibilmente tramite `appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "Default": "Server=localhost;Database=WebApiPlayground;Trusted_Connection=True;TrustServerCertificate=True;"
  }
}
```

E in `Program.cs`:

```csharp
builder.Services.AddDbContext<PlaygroundDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));
```

### 2. Verificare che dotnet-ef sia installato

```bash
dotnet ef --version
```

Se non installato:

```bash
dotnet tool install --global dotnet-ef
```

---

## Comandi

### Aggiungere una migration

```bash
dotnet ef migrations add <NomeMigration> --project WebApiPlayground/WebApiPlayground.csproj
```

Convenzioni per il nome:
- `InitialCreate` — prima migration
- `Add<Entity>Entity` — aggiunta nuova entità (es. `AddPublisherEntity`)
- `Add<Field>To<Entity>` — aggiunta campo (es. `AddIsbnToBook`)
- `Remove<Field>From<Entity>` — rimozione campo
- `Rename<Old>To<New>In<Entity>` — rinomina

### Applicare al database

```bash
dotnet ef database update --project WebApiPlayground/WebApiPlayground.csproj
```

### Applicare fino a una migration specifica

```bash
dotnet ef database update <NomeMigration> --project WebApiPlayground/WebApiPlayground.csproj
```

### Rimuovere l'ultima migration (solo se NON ancora applicata al DB)

```bash
dotnet ef migrations remove --project WebApiPlayground/WebApiPlayground.csproj
```

### Generare script SQL (senza applicare)

```bash
dotnet ef migrations script --project WebApiPlayground/WebApiPlayground.csproj --output migration.sql
```

---

## Struttura file generati

Le migrations vengono create in `WebApiPlayground/Migrations/`:

```
Migrations/
├── <Timestamp>_<NomeMigration>.cs       # Up() e Down()
├── <Timestamp>_<NomeMigration>.Designer.cs
└── PlaygroundDbContextModelSnapshot.cs  # Snapshot del modello corrente
```

Non modificare manualmente i file `.Designer.cs` e `ModelSnapshot.cs`.

---

## Troubleshooting comune

| Errore | Causa | Soluzione |
|--------|-------|-----------|
| `No DbContext was found` | Progetto non trovato | Verificare `--project` path |
| `Connection refused` | DB non raggiungibile | Verificare connection string e server SQL |
| `The migration ... has already been applied` | Migration già nel DB | Non rieseguire, usare una nuova migration |
| `Unable to create an object of type 'PlaygroundDbContext'` | DI non configurato per design-time | Aggiungere `IDesignTimeDbContextFactory` o usare connection string in `appsettings` |
