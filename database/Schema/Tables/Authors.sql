CREATE TABLE [dbo].[Authors]
(
    [Id]       INT           IDENTITY (1, 1) NOT NULL,
    [FullName] VARCHAR (100) NOT NULL,
    CONSTRAINT [PK_Authors] PRIMARY KEY CLUSTERED ([Id] ASC)
);
