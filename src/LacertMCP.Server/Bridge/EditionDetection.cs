using System;
using System.Collections.Generic;
using LacertMCP.Server.Errors;

namespace LacertMCP.Server.Bridge
{
    /// <summary>
    /// Late-bound edition probe — surfaces <c>{LacerteEdition, LacerteBuild,
    /// SdkVersion}</c> at session-status time per Todo.md task #78. Cached by
    /// the session manager so the probe runs at most once per process.
    ///
    /// <para>
    /// Implementation note: the SDK does not ship a single "GetEdition" call;
    /// the three values come from different COM properties / registry probes
    /// depending on Lacerte version. For now this returns sentinel values from
    /// sim and throws <see cref="SyntheticStatusCodes.BridgeUnavailable"/> in
    /// live mode if the bridge is null — Phase 9 will fill in the real
    /// late-bound probe sequence on the Windows + Lacerte box.
    /// </para>
    /// </summary>
    public static class EditionDetection
    {
        /// <summary>Sentinel edition string returned in sim mode.</summary>
        public const string SimulatedEdition = "Simulated";

        /// <summary>Sentinel build string returned in sim mode.</summary>
        public const string SimulatedBuild = "0.0.0-sim";

        /// <summary>Sentinel SDK version returned in sim mode.</summary>
        public const string SimulatedSdkVersion = "0.0-sim";

        /// <summary>
        /// Probe edition info. If <paramref name="bridge"/> is null returns
        /// sim sentinel values. Live: late-bound COM property reads (Phase 9
        /// wires the real calls); for now throws BridgeUnavailable on a
        /// non-null bridge to surface that the live path is not implemented.
        /// </summary>
        public static EditionInfo Probe(ILacerteBridge? bridge)
        {
            if (bridge == null)
            {
                // Sim path: return stable sentinels so SessionStatus can
                // surface them regardless of mode.
                return new EditionInfo(
                    LacerteEdition: SimulatedEdition,
                    LacerteBuild: SimulatedBuild,
                    SdkVersion: SimulatedSdkVersion);
            }

            // TODO Phase 9 task #62/65: implement the late-bound probe sequence.
            // Expected approach:
            //   1. dynamic com = bridge.ComInstance;
            //   2. var build = (string)com.LacerteBuild;     // or HostQuery-equivalent
            //   3. var edition = (string)com.LacerteEdition;
            //   4. var sdk = bridge.SdkVersion;              // const on the wrapper
            // Cached by the session manager — see LacerteSessionManager.SessionStatus().
            throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                new Dictionary<string, object?>
                {
                    ["op"] = "EditionDetection.Probe",
                    ["reason"] = "Live edition probe not yet implemented — Phase 9 fills this in.",
                });
        }
    }

    /// <summary>
    /// Edition triple surfaced in <c>lacerte_session_status</c>. All three
    /// fields are strings so non-numeric build identifiers (preview tags,
    /// sentinel sim values) flow through cleanly.
    /// </summary>
    public sealed class EditionInfo
    {
        public string LacerteEdition { get; }
        public string LacerteBuild { get; }
        public string SdkVersion { get; }

        public EditionInfo(string LacerteEdition, string LacerteBuild, string SdkVersion)
        {
            this.LacerteEdition = LacerteEdition ?? throw new ArgumentNullException(nameof(LacerteEdition));
            this.LacerteBuild = LacerteBuild ?? throw new ArgumentNullException(nameof(LacerteBuild));
            this.SdkVersion = SdkVersion ?? throw new ArgumentNullException(nameof(SdkVersion));
        }
    }
}
