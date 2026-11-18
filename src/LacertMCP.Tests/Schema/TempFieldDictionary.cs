using System;
using System.IO;
using LacertMCP.Server.Schema;

namespace LacertMCP.Tests.Schema
{
    /// <summary>
    /// Points <see cref="FieldDictionaryLoader"/> at a temp folder for the
    /// current async flow (via its AsyncLocal test seam) and removes it on
    /// dispose. Construct it synchronously inside the test body so the
    /// override flows into the code under test.
    /// </summary>
    internal sealed class TempFieldDictionary : IDisposable
    {
        public string Root { get; }

        public TempFieldDictionary()
        {
            Root = Path.Combine(Path.GetTempPath(), "lacerte-fielddict-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            FieldDictionaryLoader.RootOverrideForTests.Value = Root;
        }

        /// <summary>Writes a module file from raw entry JSON objects (each a JSON object literal).</summary>
        public void WriteRaw(char module, params string[] entryJson)
        {
            var body = "{ \"module\": \"" + module + "\", \"entries\": [" + string.Join(",", entryJson) + "] }";
            WriteText(module, body);
        }

        public void WriteText(char module, string text)
        {
            File.WriteAllText(Path.Combine(Root, Modules.FileBasenameFor(module) + ".json"), text);
            FieldDictionaryLoader.ReloadCaches();
        }

        public void Write(char module, params FieldDictionaryEntry[] entries)
            => WriteText(module, FieldDictionaryLoader.Serialize(module, entries));

        public static FieldDictionaryEntry Field(string id, int table, string field, params int[] years)
            => new FieldDictionaryEntry(id, "concept " + id, "1120", null, FieldDictionaryLoader.KindField,
                table, field, null, null, null, null,
                needsSubclient: table == 2 || table == 4, needsState: table == 3 || table == 4,
                "amount", null, years, "TY2024 on TEST-PC, 2026-11-18", FieldDictionaryLoader.SourceMapDiff);

        public static FieldDictionaryEntry Detail(string id, int series, int code, int? prefix = null, int? suffix = null, params int[] years)
            => new FieldDictionaryEntry(id, "concept " + id, "1065", "K-1 box 4", FieldDictionaryLoader.KindDetail,
                null, null, series, code, prefix, suffix, false, false,
                "amount", "a note", years, "TY2024 on TEST-PC, 2026-11-18", FieldDictionaryLoader.SourceManual);

        public void Dispose()
        {
            FieldDictionaryLoader.RootOverrideForTests.Value = null;
            FieldDictionaryLoader.ReloadCaches();
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }
}
