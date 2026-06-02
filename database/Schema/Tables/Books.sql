CREATE TABLE [dbo].[Books]
(
    [Id]       INT            IDENTITY (1, 1) NOT NULL,
    [Title]    NVARCHAR (100) NOT NULL,
    [AuthorId] INT            NOT NULL,
    CONSTRAINT [PK_Books] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_Books_Authors] FOREIGN KEY ([AuthorId])
        REFERENCES [dbo].[Authors] ([Id])
);
