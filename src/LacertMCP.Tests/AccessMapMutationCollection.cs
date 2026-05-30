using Xunit;

namespace LacertMCP.Tests
{
    /// <summary>
    /// xUnit collection that serializes every test class that mutates the
    /// <c>FieldAccessMapLoader</c> static cache or the
    /// <c>Schema/FieldAccessMap/base/*.json</c> shards on disk. Without
    /// this, parallel test execution races: one class's <c>Dispose</c>
    /// wipes the cache mid-test of another class.
    /// </summary>
    [CollectionDefinition(Name)]
    public class AccessMapMutationCollection
    {
        public const string Name = "AccessMapMutation";
    }
}
