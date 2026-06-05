CREATE TABLE [dbo].[BookPopularitySnapshots]
(
    -- PK = FK verso Books (relazione 1:1). La chiave la fornisce il worker (non IDENTITY).
    [BookId]                INT            NOT NULL,
    [AverageRating]         FLOAT          NULL,
    [RatingsCount]          INT            NULL,
    [WantToReadCount]       INT            NULL,
    [CurrentlyReadingCount] INT            NULL,
    [AlreadyReadCount]      INT            NULL,
    [ReadingLogCount]       INT            NULL,
    [Source]                VARCHAR (50)   NOT NULL,
    [RetrievedAt]           DATETIMEOFFSET NOT NULL,
    CONSTRAINT [PK_BookPopularitySnapshots] PRIMARY KEY CLUSTERED ([BookId] ASC),
    -- Cascade: cancellando il libro sparisce anche il suo snapshot (nessuna riga orfana).
    CONSTRAINT [FK_BookPopularitySnapshots_Books] FOREIGN KEY ([BookId])
        REFERENCES [dbo].[Books] ([Id]) ON DELETE CASCADE
);
