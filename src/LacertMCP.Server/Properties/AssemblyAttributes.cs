// Test-only: expose internal members of LacertMCP.Server to the test assembly.
// Required by:
//   - Tests/Schema/FieldAccessMapCoverageTests.cs (calls FieldAccessMapLoader.ResetCacheForTests)
//   - Tests/Bridge/SessionManagerBridgeWireupTests.cs (calls the internal test-only
//     LacerteSessionManager(ILacerteBridge, env) constructor — works via reflection
//     today but having the attribute makes the intent explicit)
//
// Phase 9 task #67 (Bridge wire-up) coordination note: keeping this in
// Properties/ rather than at the top of LacerteSessionManager.cs avoids
// accidental edits from concurrent streams touching the session manager file.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LacertMCP.Tests")]
