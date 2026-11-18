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
        /// Set by Program.cs in HTTP mode. There the sign-in runs in the
        /// background: hosted services start one after another, and awaiting
        /// the Lacerte COM call (which can wait on an Intuit sign-in window
        /// with a cold ticket, or on AutoReconnect back-off) held back the
        /// listener, so the control page's Reconnect was unreachable. Under
        /// stdio it is awaited as before: nothing else needs to answer first,
        /// and the client's first tool calls must not race it into 203
        /// NotSignedIn (Boundary Invariant 8: sign in at startup, not mid-tool).
        /// </summary>
        public static bool RunInBackground { get; set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (RunInBackground)
            {
                _attempt = Task.Run(() => TryRegisterAsync(_session, _log, _stop.Token));
                return;
            }
            _attempt = TryRegisterAsync(_session, _log, cancellationToken);
            await _attempt.ConfigureAwait(false);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stop.Cancel();
            return Task.CompletedTask;
        }

        /// <summary>The in-flight startup attempt (tests await it).</summary>
        internal Task? Attempt => _attempt;

        private static readonly object s_inflightGate = new object();
        private static Task<bool>? s_inflight;

        /// <summary>
        /// Attempts registration; never throws. Returns true on AccessGranted.
        /// Single-flight: a caller arriving while an attempt is running (the
        /// background startup sign-in, the control page's Reconnect, the
        /// post-login re-attach) shares it instead of starting a second COM
        /// Register — which could open a second Intuit sign-in window.
        /// </summary>
        public static Task<bool> TryRegisterAsync(LacerteSessionManager session, ILogger? log, CancellationToken ct)
        {
            lock (s_inflightGate)
            {
                if (s_inflight != null && !s_inflight.IsCompleted) return s_inflight;
                s_inflight = TryRegisterCoreAsync(session, log, ct);
                return s_inflight;
            }
        }

        private static async Task<bool> TryRegisterCoreAsync(LacerteSessionManager session, ILogger? log, CancellationToken ct)
        {
            await Task.Yield(); // leave the lock before the COM call starts
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
