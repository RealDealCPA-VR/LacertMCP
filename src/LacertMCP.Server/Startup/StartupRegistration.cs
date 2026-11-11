using System;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LacertMCP.Server.Startup
{
    /// <summary>
    /// Runs <see cref="LacerteSessionManager.Register"/> once when the server
    /// starts (ARCHITECTURE Boundary Invariant 8: "Registration … called once
    /// at startup"). Before this existed nothing outside the test suite ever
    /// called Register(), so every read/write tool failed with 203 NotSignedIn
    /// in BOTH modes — the server could not serve a single data call.
    ///
    /// <para>
    /// Live: this re-attaches to the cached 24h Intuit login created by
    /// <c>lacerte-mcp register</c> / <c>lacerte-mcp login</c>. It never runs the
    /// interactive ceremony. A failure is logged with the remediation and left
    /// for the operator (control page "Reconnect to Lacerte", or the CLI) —
    /// tools keep returning the structured NotSignedIn / AppNotRegistered error.
    /// </para>
    /// </summary>
    public sealed class StartupRegistration : IHostedService
    {
        private readonly LacerteSessionManager _session;
        private readonly ILogger<StartupRegistration> _log;

        public StartupRegistration(LacerteSessionManager session, ILogger<StartupRegistration> log)
        {
            _session = session;
            _log = log;
        }

        /// <summary>Result of the most recent attempt, for the control page.</summary>
        public static string? LastResult { get; private set; }
        public static DateTime? LastAttemptAt { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await TryRegisterAsync(_session, _log, cancellationToken).ConfigureAwait(false);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>Attempts registration; never throws. Returns true on AccessGranted.</summary>
        public static async Task<bool> TryRegisterAsync(LacerteSessionManager session, ILogger? log, CancellationToken ct)
        {
            LastAttemptAt = DateTime.UtcNow;
            try
            {
                var status = await session.Register(ct).ConfigureAwait(false);
                var info = StatusCodes.GetStatusInfo(status);
                if (status == StatusCodes.AccessGranted)
                {
                    LastResult = "Connected to Lacerte (" + session.Mode.ToString().ToLowerInvariant() + " mode).";
                    log?.LogInformation("[startup] Lacerte registration: AccessGranted (mode={Mode})", session.Mode);
                    return true;
                }
                LastResult = "Lacerte did not grant access: " + info.Name + ". " + info.Remediation;
                log?.LogWarning("[startup] Lacerte registration returned {Status} {Name}: {Remediation}", status, info.Name, info.Remediation);
                return false;
            }
            catch (LacerteSDKError ex)
            {
                LastResult = ex.StatusName + ": " + ex.UserMessage + " " + ex.Remediation;
                log?.LogWarning("[startup] Lacerte registration failed: {Status} {Message} {Remediation}", ex.StatusName, ex.UserMessage, ex.Remediation);
                return false;
            }
            catch (Exception ex)
            {
                LastResult = "Registration failed: " + ex.Message;
                log?.LogWarning(ex, "[startup] Lacerte registration threw");
                return false;
            }
        }
    }
}
