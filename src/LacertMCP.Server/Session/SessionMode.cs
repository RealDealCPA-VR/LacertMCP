namespace LacertMCP.Server.Session
{
    /// <summary>
    /// One of two operating modes for the session manager. Resolved once at
    /// construction time from the environment + platform and immutable for the
    /// life of the process — see <see cref="LacerteSessionManager"/> and
    /// <c>ARCHITECTURE.md</c> "Operating Modes".
    /// </summary>
    public enum SessionMode
    {
        /// <summary>
        /// All requests served from the in-memory <see cref="SimulationStore"/>.
        /// Default on non-Windows and whenever <c>LACERTE_SIMULATION=1</c> is set.
        /// </summary>
        Simulation,

        /// <summary>
        /// Real COM + ODBC against the installed Lacerte SDK. Only available on
        /// Windows when <c>LACERTE_LIVE=1</c> AND <c>LACERTE_SIMULATION != "1"</c>.
        /// </summary>
        Live,
    }
}
