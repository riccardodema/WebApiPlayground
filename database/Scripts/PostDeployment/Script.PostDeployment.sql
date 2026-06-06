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
    (1,  N'Dino Buzzati'),
    (2,  N'Franz Kafka'),
    (3,  N'Herman Melville'),
    (4,  N'Italo Calvino'),
    (5,  N'Umberto Eco'),
    (6,  N'Fëdor Dostoevskij'),
    (7,  N'Gabriel García Márquez'),
    (8,  N'George Orwell'),
    (9,  N'Jane Austen'),
    (10, N'Jorge Luis Borges'),
    (11, N'Haruki Murakami'),
    (12, N'Virginia Woolf'),
    (13, N'Lev Tolstoj'),
    (14, N'Ernest Hemingway'),
    (15, N'Gabriele D''Annunzio'),
    (16, N'Cesare Pavese'),
    (17, N'Primo Levi'),
    (18, N'Elsa Morante'),
    (19, N'Alessandro Manzoni'),
    (20, N'Luigi Pirandello'),
    (21, N'Italo Svevo'),
    (22, N'Giovanni Verga'),
    (23, N'Albert Camus'),
    (24, N'Marcel Proust'),
    (25, N'James Joyce'),
    (26, N'Thomas Mann'),
    (27, N'Hermann Hesse'),
    (28, N'J.R.R. Tolkien'),
    (29, N'George R.R. Martin'),
    (30, N'Stephen King'),
    (31, N'Agatha Christie'),
    (32, N'Arthur Conan Doyle'),
    (33, N'Oscar Wilde'),
    (34, N'Charles Dickens'),
    (35, N'Mark Twain'),
    (36, N'J.D. Salinger'),
    (37, N'Kurt Vonnegut'),
    (38, N'Philip K. Dick'),
    (39, N'Isaac Asimov'),
    (40, N'Ray Bradbury'),
    (41, N'Alexandre Dumas'),
    (42, N'Victor Hugo'),
    (43, N'Miguel de Cervantes'),
    (44, N'Johann Wolfgang von Goethe'),
    (45, N'Jules Verne')
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
    (1,   N'Il deserto dei tartari',                     1),
    (2,   N'Il processo',                                2),
    (3,   N'Moby Dick',                                  3),
    (4,   N'Il castello',                                2),
    (5,   N'La metamorfosi',                             2),
    (6,   N'Il barone rampante',                         4),
    (7,   N'Le città invisibili',                        4),
    (8,   N'Se una notte d''inverno un viaggiatore',     4),
    (9,   N'Il nome della rosa',                         5),
    (10,  N'Il pendolo di Foucault',                     5),
    (11,  N'Delitto e castigo',                          6),
    (12,  N'I fratelli Karamazov',                       6),
    (13,  N'L''idiota',                                  6),
    (14,  N'Cent''anni di solitudine',                   7),
    (15,  N'L''amore ai tempi del colera',               7),
    (16,  N'1984',                                       8),
    (17,  N'La fattoria degli animali',                  8),
    (18,  N'Orgoglio e pregiudizio',                     9),
    (19,  N'Emma',                                       9),
    (20,  N'Finzioni',                                   10),
    (21,  N'Norwegian Wood',                             11),
    (22,  N'Kafka sulla spiaggia',                       11),
    (23,  N'La signora Dalloway',                        12),
    (24,  N'Gita al faro',                               12),
    (25,  N'Il visconte dimezzato',                      4),
    (26,  N'Il cavaliere inesistente',                   4),
    (27,  N'Marcovaldo',                                 4),
    (28,  N'La coscienza di Zeno',                       21),
    (29,  N'Senilità',                                   21),
    (30,  N'Una vita',                                   21),
    (31,  N'Il fu Mattia Pascal',                        20),
    (32,  N'Uno, nessuno e centomila',                   20),
    (33,  N'Sei personaggi in cerca d''autore',          20),
    (34,  N'I Malavoglia',                               22),
    (35,  N'Mastro-don Gesualdo',                        22),
    (36,  N'I promessi sposi',                           19),
    (37,  N'Guerra e pace',                              13),
    (38,  N'Anna Karenina',                              13),
    (39,  N'La morte di Ivan Il''ic',                    13),
    (40,  N'Il vecchio e il mare',                       14),
    (41,  N'Addio alle armi',                            14),
    (42,  N'Per chi suona la campana',                   14),
    (43,  N'Il piacere',                                 15),
    (44,  N'Il fuoco',                                   15),
    (45,  N'La luna e i falò',                           16),
    (46,  N'Il mestiere di vivere',                      16),
    (47,  N'Se questo è un uomo',                        17),
    (48,  N'La tregua',                                  17),
    (49,  N'Il sistema periodico',                       17),
    (50,  N'La Storia',                                  18),
    (51,  N'L''isola di Arturo',                         18),
    (52,  N'Lo straniero',                               23),
    (53,  N'La peste',                                   23),
    (54,  N'Il mito di Sisifo',                          23),
    (55,  N'Alla ricerca del tempo perduto',             24),
    (56,  N'Ulisse',                                     25),
    (57,  N'Gente di Dublino',                           25),
    (58,  N'La montagna incantata',                      26),
    (59,  N'La morte a Venezia',                         26),
    (60,  N'Siddharta',                                  27),
    (61,  N'Il lupo della steppa',                       27),
    (62,  N'Narciso e Boccadoro',                        27),
    (63,  N'Il signore degli anelli',                    28),
    (64,  N'Lo Hobbit',                                  28),
    (65,  N'Il Silmarillion',                            28),
    (66,  N'Il trono di spade',                          29),
    (67,  N'Lo scontro dei re',                          29),
    (68,  N'It',                                         30),
    (69,  N'Shining',                                    30),
    (70,  N'Misery',                                     30),
    (71,  N'Assassinio sull''Orient Express',            31),
    (72,  N'Dieci piccoli indiani',                      31),
    (73,  N'Poirot a Styles Court',                      31),
    (74,  N'Il mastino dei Baskerville',                 32),
    (75,  N'Uno studio in rosso',                        32),
    (76,  N'Il ritratto di Dorian Gray',                 33),
    (77,  N'L''importanza di chiamarsi Ernesto',         33),
    (78,  N'Grandi speranze',                            34),
    (79,  N'David Copperfield',                          34),
    (80,  N'Oliver Twist',                               34),
    (81,  N'Le avventure di Tom Sawyer',                 35),
    (82,  N'Le avventure di Huckleberry Finn',           35),
    (83,  N'Il giovane Holden',                          36),
    (84,  N'Mattatoio n. 5',                             37),
    (85,  N'Ghiaccio-nove',                              37),
    (86,  N'Ma gli androidi sognano pecore elettriche?', 38),
    (87,  N'L''uomo nell''alto castello',                38),
    (88,  N'Io, robot',                                  39),
    (89,  N'Fondazione',                                 39),
    (90,  N'Fahrenheit 451',                             40),
    (91,  N'Cronache marziane',                          40),
    (92,  N'Il Conte di Montecristo',                    41),
    (93,  N'I tre moschettieri',                         41),
    (94,  N'I miserabili',                               42),
    (95,  N'Notre-Dame de Paris',                        42),
    (96,  N'Don Chisciotte della Mancia',                43),
    (97,  N'I dolori del giovane Werther',               44),
    (98,  N'Faust',                                      44),
    (99,  N'Il giro del mondo in 80 giorni',             45),
    (100, N'Ventimila leghe sotto i mari',               45)
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
