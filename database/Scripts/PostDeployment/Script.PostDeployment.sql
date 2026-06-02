/*
    Post-Deployment Script
    -----------------------
    Runs after every publish of the DACPAC. Must be idempotent: it is executed
    on first deploy AND on every subsequent re-deploy. We use MERGE so existing
    rows are updated and missing rows inserted, and IDENTITY_INSERT to keep the
    seed Ids stable (they are referenced by Books.AuthorId).
*/

PRINT N'Seeding reference data (Authors, Books)...';

SET IDENTITY_INSERT [dbo].[Authors] ON;

MERGE INTO [dbo].[Authors] AS [target]
USING (VALUES
    (1, 'Dino Buzzati'),
    (2, 'Franz Kafka'),
    (3, 'Herman Melville')
) AS [source] ([Id], [FullName])
    ON [target].[Id] = [source].[Id]
WHEN MATCHED THEN
    UPDATE SET [target].[FullName] = [source].[FullName]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Id], [FullName]) VALUES ([source].[Id], [source].[FullName]);

SET IDENTITY_INSERT [dbo].[Authors] OFF;

SET IDENTITY_INSERT [dbo].[Books] ON;

MERGE INTO [dbo].[Books] AS [target]
USING (VALUES
    (1, N'Il deserto dei tartari', 1),
    (2, N'Il processo',            2),
    (3, N'Moby Dick',              3)
) AS [source] ([Id], [Title], [AuthorId])
    ON [target].[Id] = [source].[Id]
WHEN MATCHED THEN
    UPDATE SET [target].[Title]    = [source].[Title],
               [target].[AuthorId] = [source].[AuthorId]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Id], [Title], [AuthorId])
    VALUES ([source].[Id], [source].[Title], [source].[AuthorId]);

SET IDENTITY_INSERT [dbo].[Books] OFF;

PRINT N'Seed completed.';
