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
    /// Live: this makes the same SDK <c>Register</c> call as
    /// <c>lacerte-mcp login</c>. With a valid cached 24h Intuit login it
    /// re-attaches silently; with a cold ticket the SDK may show its sign-in
    /// window on the desktop, so the call runs in the background and never
    /// blocks server startup. A failure is logged with the remediation and left
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

        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Task? _attempt;

        /// <summary>
        /// Starts the sign-in attempt in the background and returns at once.
        /// Hosted services start one after another, so awaiting Register here
        /// held back the HTTP listener and the stdio transport until the
        /// Lacerte COM call returned — and that call can wait on an Intuit
        /// sign-in window when the 24h ticket is cold, or on AutoReconnect
        /// back-off. Tools answer NotSignedIn / LoginExpired until it lands,
        /// and the control page's Reconnect stays reachable meanwhile.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _attempt = Task.Run(() => TryRegisterAsync(_session, _log, _stop.Token));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stop.Cancel();
            return Task.CompletedTask;
        }

        /// <summary>The in-flight startup attempt (tests await it).</summary>
        internal Task? Attempt => _attempt;

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
