namespace IntegrationTests.Shared.Fixtures;

/// <summary>
/// Collection definition for all daemon integration tests.
/// This ensures tests run sequentially and share a single DaemonTestFixture instance,
/// preventing port conflicts when multiple Aspire applications try to start.
/// </summary>
[CollectionDefinition(Name)]
public class DaemonTestCollection : ICollectionFixture<DaemonTestFixture>
{
    public const string Name = "Daemon Integration Tests";
}
