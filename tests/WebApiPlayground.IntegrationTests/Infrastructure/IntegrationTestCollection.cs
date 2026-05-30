using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

[CollectionDefinition("Integration")]
public class IntegrationTestCollection : ICollectionFixture<PlaygroundApiFactory> { }
