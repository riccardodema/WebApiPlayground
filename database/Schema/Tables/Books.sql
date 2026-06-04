CREATE TABLE [dbo].[Books]
(
    [Id]         INT            IDENTITY (1, 1) NOT NULL,
    [Title]      NVARCHAR (100) NOT NULL,
    [AuthorId]   INT            NOT NULL,
    -- Optimistic concurrency token: auto-mantenuto da SQL Server a ogni UPDATE della riga.
    -- Mappato in EF con .IsRowVersion() ed esposto in HTTP come ETag (If-Match → 412/428).
    [RowVersion] ROWVERSION     NOT NULL,
    CONSTRAINT [PK_Books] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_Books_Authors] FOREIGN KEY ([AuthorId])
        REFERENCES [dbo].[Authors] ([Id])
);
