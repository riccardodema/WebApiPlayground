# /run — Avvio del progetto

Avvia WebApiPlayground in locale e verifica che l'API sia raggiungibile.

---

## Prerequisiti

### Connection string

La connection string in `Program.cs:25` è un placeholder (`"stringaacaso"`).
Prima di avviare, verificare se è necessaria una connessione DB reale.

**Opzione A — Solo sviluppo senza DB** (se non si testano endpoint che leggono dal DB):
Avviare normalmente; Swagger sarà accessibile ma le chiamate al DB falliranno a runtime.

**Opzione B — Con DB reale**:
Aggiornare `appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "Default": "Server=localhost;Database=WebApiPlayground;Trusted_Connection=True;TrustServerCertificate=True;"
  }
}
```

E `Program.cs`:

```csharp
options.UseSqlServer(builder.Configuration.GetConnectionString("Default"))
```

Poi applicare le migrations: `/migration`

---

## Avvio

```bash
dotnet run --project WebApiPlayground/WebApiPlayground.csproj
```

Oppure dalla cartella `WebApiPlayground/`:

```bash
dotnet run
```

Con profilo specifico:

```bash
dotnet run --launch-profile https
dotnet run --launch-profile http
```

---

## URL disponibili

| Profilo | HTTP | HTTPS | Swagger |
|---------|------|-------|---------|
| `https` | — | `https://localhost:7123` | `https://localhost:7123/swagger` |
| `http` | `http://localhost:5242` | — | `http://localhost:5242/swagger` |

---

## Endpoint attivi

| Metodo | Path | Descrizione |
|--------|------|-------------|
| `GET` | `/api/books` | Tutti i libri |
| `GET` | `/api/books/{id}` | Libro per ID |
| `DELETE` | `/api/books` | **Non implementato** — restituisce 200 senza effetti |

---

## Hot reload (sviluppo)

```bash
dotnet watch --project WebApiPlayground/WebApiPlayground.csproj
```

Riavvia automaticamente ad ogni modifica al codice.

---

## Verifica rapida

Dopo l'avvio, testare con curl o Swagger UI:

```bash
# Lista libri
curl -k https://localhost:7123/api/books

# Libro per ID
curl -k https://localhost:7123/api/books/1
```

Risposta attesa per un DB vuoto: `[]`
Risposta attesa per ID inesistente: `404 Not Found`
