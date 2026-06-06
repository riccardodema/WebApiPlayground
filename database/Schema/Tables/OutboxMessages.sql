CREATE TABLE [dbo].[OutboxMessages]
(
    -- Coda durevole degli eventi di integrazione: scritta nella STESSA transazione della write di
    -- business (transactional outbox) e drenata dal dispatcher in background (at-least-once).
    [Id]          BIGINT          IDENTITY (1, 1) NOT NULL,
    [Type]        VARCHAR (200)   NOT NULL,            -- discriminatore evento (routing nel dispatcher)
    [Payload]     NVARCHAR (MAX)  NOT NULL,            -- evento serializzato (JSON)
    [OccurredAt]  DATETIMEOFFSET  NOT NULL,            -- quando l'evento è stato prodotto
    [ProcessedAt] DATETIMEOFFSET  NULL,                -- NULL = ancora da processare; valorizzato a successo
    [Attempts]    INT             NOT NULL CONSTRAINT [DF_OutboxMessages_Attempts] DEFAULT (0),
    [Error]       NVARCHAR (MAX)  NULL,                -- ultimo errore (diagnostica dei retry)
    CONSTRAINT [PK_OutboxMessages] PRIMARY KEY CLUSTERED ([Id] ASC)
);
GO
-- Indice filtrato: il poll del dispatcher scandisce solo i messaggi non ancora processati, non l'intera
-- tabella che cresce con lo storico. ORDER BY Id (FIFO) servito dalla chiave dell'indice.
CREATE NONCLUSTERED INDEX [IX_OutboxMessages_Unprocessed]
    ON [dbo].[OutboxMessages] ([Id] ASC)
    WHERE [ProcessedAt] IS NULL;
