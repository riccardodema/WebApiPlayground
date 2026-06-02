# Database versionato — SQL Project / DACPAC

Lo schema del DB è versionato nella soluzione come **SQL Database Project**
(SDK `Microsoft.Build.Sql`), in `database/`. Schema **dichiarativo**: un `.sql`
per oggetto. La build produce un **DACPAC**; il rilascio applica il diff con SqlPackage.

Progetto incluso in `WebApiPlayground.sln` sotto la solution folder `database`.

## Struttura

```
database/
├── WebApiPlayground.Database.sqlproj      # progetto (SDK Microsoft.Build.Sql 2.1.0)
├── WebApiPlayground.Database.publish.xml   # opzioni di deploy (NO credenziali)
├── deploy.sh                               # build + publish|script
├── Schema/Tables/Authors.sql               # dbo.Authors (PK_Authors)
├── Schema/Tables/Books.sql                 # dbo.Books  (PK_Books, FK_Books_Authors)
└── Scripts/PostDeployment/Script.PostDeployment.sql  # seed idempotente (MERGE)
```

## Regole

- **Fonte di verità = SQL project.** Il modello EF (`PlaygroundDbContext.OnModelCreating`)
  è mappato 1:1: tipi (`varchar(100)` FullName, `nvarchar(100)` Title), `IsRequired`,
  FK `FK_Books_Authors` con `DeleteBehavior.NoAction`. Se cambi lo schema, aggiorna entrambi.
- I file in `Scripts/**` NON sono schema: sono esclusi dal Build nel `.sqlproj`
  (`<Build Remove="Scripts/**/*.sql" />`) e dichiarati come `<PostDeploy>`.
- Il post-deploy gira a **ogni** publish → deve restare idempotente (`MERGE` + `IDENTITY_INSERT`).
- DSP = `Sql150` (Azure SQL Edge / SQL Server 2019, engine 15.x).

## Comandi

```bash
# Build → DACPAC (database/bin/<cfg>/WebApiPlayground.Database.dacpac)
dotnet build database/WebApiPlayground.Database.sqlproj -c Release

# Prerequisito rilascio (una tantum)
dotnet tool install -g Microsoft.SqlPackage

# Rilascio (connection string via env, niente password nel repo)
export DB_CONNECTION='Server=localhost;Database=PlaygroundDatabase;User ID=sa;Password=*****;TrustServerCertificate=True;'
./database/deploy.sh          # publish
./database/deploy.sh script   # genera solo lo script di migrazione da rivedere
```

## Aggiungere un oggetto (es. tabella)

1. Crea `database/Schema/Tables/<Nome>.sql` con la `CREATE TABLE` dichiarativa.
2. Aggiorna l'entità/`OnModelCreating` EF corrispondente (mapping 1:1).
3. `dotnet build` del .sqlproj per validare il modello, poi `deploy.sh script` per
   vedere il diff prima di applicarlo.
