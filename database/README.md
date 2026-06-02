# WebApiPlayground — Database (SQL Project / DACPAC)

Schema del database **versionato nella soluzione** come SQL Database Project
(SDK `Microsoft.Build.Sql`). Lo schema è **dichiarativo**: ogni oggetto è un file
`.sql` con la sua `CREATE`. La build produce un **DACPAC**; il rilascio lo applica
a un database calcolando automaticamente il diff (nessuno script di ALTER scritto a mano).

Questo progetto (`WebApiPlayground.Database.sqlproj`) è incluso in
`WebApiPlayground.sln`, sotto la solution folder `database`.

## Struttura

```
database/
├── WebApiPlayground.Database.sqlproj   # progetto (nel .sln)
├── WebApiPlayground.Database.publish.xml# opzioni di deploy (no credenziali)
├── deploy.sh                            # helper build + publish/script
├── Schema/
│   └── Tables/
│       ├── Authors.sql                  # dbo.Authors
│       └── Books.sql                    # dbo.Books (FK_Books_Authors)
└── Scripts/
    └── PostDeployment/
        └── Script.PostDeployment.sql    # seed idempotente (MERGE) eseguito a ogni publish
```

> Lo schema qui è la **fonte di verità**. Il modello EF Core
> (`PlaygroundDbContext`) è mappato 1:1 con queste tabelle (tipi, lunghezze, FK):
> se cambi lo schema, aggiorna entrambi.

## Build (produce il DACPAC)

```bash
dotnet build database/WebApiPlayground.Database.sqlproj -c Release
# output: database/bin/Release/WebApiPlayground.Database.dacpac
```

Viene costruito anche con `dotnet build WebApiPlayground.sln`.

## Prerequisito di rilascio: SqlPackage

```bash
dotnet tool install -g Microsoft.SqlPackage   # una tantum
# assicurati che ~/.dotnet/tools sia nel PATH
```

## Rilascio

Lo script legge la connection string da `DB_CONNECTION` (così nessuna password
finisce nel repo):

```bash
export DB_CONNECTION='Server=localhost;Database=PlaygroundDatabase;User ID=sa;Password=*****;TrustServerCertificate=True;'

# applica direttamente
./database/deploy.sh

# oppure genera solo lo script di migrazione da rivedere prima di applicarlo
./database/deploy.sh script   # -> database/bin/Release/WebApiPlayground.Database.DeployScript.sql
```

In alternativa, comando `sqlpackage` diretto:

```bash
sqlpackage /Action:Publish \
  /SourceFile:database/bin/Release/WebApiPlayground.Database.dacpac \
  /Profile:database/WebApiPlayground.Database.publish.xml \
  /TargetConnectionString:"$DB_CONNECTION"
```

Il profilo (`*.publish.xml`) imposta opzioni di sicurezza: `BlockOnPossibleDataLoss=True`
e `DropObjectsNotInSource=False` (non droppa oggetti non presenti nel progetto).

## Seed

`Script.PostDeployment.sql` gira **dopo ogni publish** ed è idempotente (usa `MERGE`
+ `IDENTITY_INSERT`), quindi è sicuro rieseguirlo: aggiorna le righe esistenti e
inserisce quelle mancanti, mantenendo stabili gli Id (referenziati da `Books.AuthorId`).
