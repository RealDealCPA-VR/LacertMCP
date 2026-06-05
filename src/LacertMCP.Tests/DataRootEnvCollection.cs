using Xunit;

namespace LacertMCP.Tests
{
    /// <summary>
    /// xUnit collection that serializes every test class that mutates the
    /// process-wide <c>LACERTE_DATA_ROOT</c> env variable. Without this,
    /// parallel test execution races: a FirmDashboard test sets the root
    /// to a temp dir mid-run, and a sibling test class (e.g.
    /// PreFilingCheck) that calls <c>SetContext</c> with the canonical
    /// <c>C:\Lacerte\24Tax\IData</c> path eats a 9013 InvalidDataPath
    /// because the canonical seed path is no longer under the (now-temp)
    /// LACERTE_DATA_ROOT allowlist.
    ///
    /// <para>
    /// Apply via <c>[Collection(DataRootEnvCollection.Name)]</c> on any
    /// test class that reads or writes <c>LACERTE_DATA_ROOT</c>.
    /// </para>
    /// </summary>
    [CollectionDefinition(Name)]
    public class DataRootEnvCollection
    {
        public const string Name = "DataRootEnv";
    }
}
