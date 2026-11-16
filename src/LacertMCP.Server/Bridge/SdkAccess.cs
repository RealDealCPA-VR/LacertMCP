using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using LacertMCP.Server.Errors;

namespace LacertMCP.Server.Bridge
{
    /// <summary>
    /// R7-SRC — single entry point for the Lacerte SDK COM object graph, per
    /// the Intuit "Lacerte Software Development Kit Developer Instructions"
    /// (static.developer.intuit.com/resources/Lacerte_SDK_Developer_Instructions.pdf):
    ///
    /// <list type="bullet">
    ///   <item><description>The ONLY directly-creatable class is
    ///     <c>LacerteSDK_Access.LacerteAccess</c> (VB: <c>New LacerteSDK_Access.LacerteAccess</c>;
    ///     C#: <c>new LacerteAccess()</c>).</description></item>
    ///   <item><description>Every other object comes from an
    ///     <c>ILacerteAccess</c> factory method: <c>CreateTPRegister</c>
    ///     (ITPRegistration), <c>CreateDBFReader</c> (IDBFReader),
    ///     <c>CreateDBFWriter</c> (IDBFWriter), <c>CreateClient</c> (IClient),
    ///     <c>CreateClientList</c> (IClientList).</description></item>
    /// </list>
    ///
    /// The bridge previously activated undocumented ProgIDs
    /// (<c>LacerteSDK_Access.TPRegistration</c> / <c>.DBFReader</c> /
    /// <c>.DBFWriter</c>), which the SDK does not register — every live
    /// register / read / write would have failed with "ProgID not registered".
    /// </summary>
    internal static class SdkAccess
    {
        internal const string LacerteAccessProgId = "LacerteSDK_Access.LacerteAccess";

        /// <summary>
        /// A LacerteAccess RCW plus one object obtained from it. Dispose
        /// releases both (child first) — P14-004 per-call RCW release.
        /// </summary>
        internal sealed class Handle : IDisposable
        {
            public object Access { get; }
            public object Target { get; }

            public Handle(object access, object target)
            {
                Access = access;
                Target = target;
            }

            public void Dispose()
            {
                Release(Target);
                if (!ReferenceEquals(Target, Access)) Release(Access);
            }
        }

        /// <summary>Creates <c>LacerteSDK_Access.LacerteAccess</c>.</summary>
        internal static object CreateLacerteAccess(string op)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["reason"] = "The Lacerte SDK requires Windows.",
                        ["platform"] = Environment.OSVersion.Platform.ToString(),
                    });
            }
            try
            {
                var t = Type.GetTypeFromProgID(LacerteAccessProgId);
                if (t == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["reason"] = LacerteAccessProgId + " ProgID not registered. Install the Lacerte SDK and run `lacerte-mcp doctor`.",
                        });
                }
                var instance = Activator.CreateInstance(t);
                if (instance == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["reason"] = "Activator.CreateInstance returned null for " + LacerteAccessProgId + ".",
                        });
                }
                return instance;
            }
            catch (LacerteSDKError) { throw; }
            catch (Exception ex)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?> { ["op"] = op, ["reason"] = ex.Message }, ex);
            }
        }

        /// <summary>
        /// Creates LacerteAccess and invokes one of its documented factory
        /// methods (CreateTPRegister / CreateDBFReader / CreateDBFWriter).
        /// </summary>
        internal static Handle Acquire(string op, string factoryMethod)
        {
            var access = CreateLacerteAccess(op);
            try
            {
                dynamic a = access;
                object? target = factoryMethod switch
                {
                    "CreateTPRegister" => (object?)a.CreateTPRegister(),
                    "CreateDBFReader" => (object?)a.CreateDBFReader(),
                    "CreateDBFWriter" => (object?)a.CreateDBFWriter(),
                    _ => throw new ArgumentOutOfRangeException(nameof(factoryMethod), factoryMethod, "Unknown ILacerteAccess factory."),
                };
                if (target == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["reason"] = "ILacerteAccess." + factoryMethod + "() returned null.",
                        });
                }
                return new Handle(access, target);
            }
            catch (LacerteSDKError)
            {
                Release(access);
                throw;
            }
            catch (Exception ex)
            {
                Release(access);
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["factory"] = factoryMethod,
                        ["reason"] = ex.Message,
                    }, ex);
            }
        }

        internal static void Release(object? rcw)
        {
            if (rcw == null) return;
            try
            {
                if (Marshal.IsComObject(rcw)) Marshal.ReleaseComObject(rcw);
            }
            catch (Exception) { /* best-effort */ }
        }

        /// <summary>
        /// R7-SRC — the SDK's write parameters are typed <c>String</c>
        /// ("6. String – Value (Value to be written)" for both
        /// WriteToDBFDatabase and UpdateTaxReturn). Late-bound IDispatch does
        /// not coerce a boxed double/bool/JsonElement into a String parameter,
        /// so render scalars invariantly here. null → empty string.
        /// </summary>
        internal static string ToSdkValueString(object? value)
        {
            switch (value)
            {
                case null: return string.Empty;
                case string s: return s;
                case bool b: return b ? "True" : "False";
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case decimal m: return m.ToString(CultureInfo.InvariantCulture);
                case IFormattable fmt: return fmt.ToString(null, CultureInfo.InvariantCulture);
                case JsonElement je:
                    switch (je.ValueKind)
                    {
                        case JsonValueKind.String: return je.GetString() ?? string.Empty;
                        case JsonValueKind.Null:
                        case JsonValueKind.Undefined: return string.Empty;
                        case JsonValueKind.True: return "True";
                        case JsonValueKind.False: return "False";
                        default: return je.GetRawText();
                    }
                default: return value.ToString() ?? string.Empty;
            }
        }

        /// <summary>
        /// R7-SRC — <c>TPSDKStatus</c> is documented as an enum whose first
        /// member is <c>ConsentDenied = 0</c>, followed (in order) by
        /// AccessGranted, VersionBlocked, NotSignedIn, LacerteTaxSetupRequired
        /// — C# enum semantics make those 1..4. The server namespaces them at
        /// 200..204 (StatusCodes) to avoid colliding with TAPIStatus 0..4, so
        /// the raw COM value must be translated; comparing the raw value with
        /// StatusCodes.AccessGranted (201) meant a successful live
        /// registration was never recognised.
        /// </summary>
        internal static int MapTpsdkStatus(int raw)
        {
            if (raw >= 0 && raw <= 4) return StatusCodes.ConsentDenied + raw;
            // Already namespaced (defensive) or an unknown future member:
            // surface it unchanged so the caller's "not AccessGranted" path runs.
            return raw;
        }
    }
}
