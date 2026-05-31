using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using LacertMCP.Server.Errors;

namespace LacertMCP.Server.Bridge
{
    /// <summary>
    /// Wraps the <c>ITPRegistration</c> interactions. Called ONCE at MCP
    /// startup per Boundary Invariant #8 — never mid-tool.
    ///
    /// <para>
    /// The actual COM call is late-bound via <c>Type.GetTypeFromProgID</c> +
    /// <c>dynamic</c> so this code compiles on machines without the Lacerte
    /// SDK installed. Off-Windows or when the ProgID resolves to null this
    /// throws <see cref="SyntheticStatusCodes.BridgeUnavailable"/>.
    /// </para>
    ///
    /// <para>
    /// AppID is read from Windows Credential Manager. The Server assembly
    /// does NOT reference the Cli assembly (Cli references Server, not the
    /// other way around), so this file duplicates the minimal CredRead path
    /// from <c>LacertMCP.Cli.Storage.AppIdStore.Get()</c>. Kept in sync with
    /// that file at the same target constant.
    /// </para>
    ///
    /// <para>
    /// Live verification deferred to Windows + Lacerte box — see
    /// <c>REGRESSION_CHECKLIST.md</c> "Manual Live-Mode Verification".
    /// </para>
    /// </summary>
    public static class Registration
    {
        // Mirrors LacertMCP.Cli.Storage.AppIdStore constants. Registration only
        // reads the credential (TryCredRead uses TargetName only), so the account
        // half lives on the Cli writer side and is not duplicated here.
        public const string CredentialTarget = "lacerte-mcp";

        /// <summary>
        /// Calls <c>ITPRegistration.Register(appName, appId, taxYear)</c> via
        /// late binding. Returns the TAPIStatus / TPSDKStatus integer
        /// (201 = AccessGranted, 203 = NotSignedIn). The session manager
        /// invokes this exactly once in the live-mode constructor branch.
        /// </summary>
        public static int Register(string appName, string appId, int taxYear)
        {
            if (!IsWindows())
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["op"] = "Registration.Register",
                        ["reason"] = "ITPRegistration.Register requires Windows + the Lacerte SDK.",
                        ["platform"] = Environment.OSVersion.Platform.ToString(),
                    });
            }

            // Live verification deferred to Windows + Lacerte box.
            object? regInstance;
            try
            {
                var t = Type.GetTypeFromProgID("LacerteSDK_Access.TPRegistration");
                if (t == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "Registration.Register",
                            ["reason"] = "LacerteSDK_Access.TPRegistration ProgID not registered. Install the Lacerte SDK and run `lacerte-mcp doctor`.",
                        });
                }
                regInstance = Activator.CreateInstance(t);
            }
            catch (LacerteSDKError)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["op"] = "Registration.Register",
                        ["reason"] = ex.Message,
                    }, ex);
            }

            try
            {
                // Late-bound call against ITPRegistration.Register(AppName, AppID, TaxYear).
                dynamic reg = regInstance!;
                int status = (int)reg.Register(appName, appId, taxYear);
                return status;
            }
            catch (Exception ex)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["op"] = "Registration.Register",
                        ["appName"] = appName,
                        ["taxYear"] = taxYear,
                        ["reason"] = ex.Message,
                    }, ex);
            }
        }

        /// <summary>
        /// Reads the persisted AppID GUID. Mirrors
        /// <c>LacertMCP.Cli.Storage.AppIdStore.Get()</c> but duplicates the
        /// minimum surface here to avoid a Server → Cli project reference
        /// (Cli already references Server, and reversing that would create a
        /// dependency cycle).
        /// </summary>
        public static Guid? GetStoredAppId()
        {
            if (IsWindows() && Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE") != "1")
            {
                if (TryCredRead(CredentialTarget, out var raw) && Guid.TryParse(raw, out var g))
                {
                    return g;
                }
                return null;
            }
            return ReadFromFile();
        }

        // -----------------------------------------------------------------
        // File fallback (off-Windows / forced file mode for tests).
        // Shape matches LacertMCP.Cli.Storage.AppIdStore.ConfigShape.
        // -----------------------------------------------------------------

        private sealed class ConfigShape
        {
            public string? AppId { get; set; }
        }

        private static Guid? ReadFromFile()
        {
            var path = FallbackConfigPath();
            if (!File.Exists(path)) return null;
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<ConfigShape>(json);
                if (cfg?.AppId == null) return null;
                return Guid.TryParse(cfg.AppId, out var g) ? (Guid?)g : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string FallbackConfigPath()
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(appData))
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            if (string.IsNullOrEmpty(appData))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home))
                {
                    home = Directory.GetCurrentDirectory();
                }
                appData = Path.Combine(home, ".config");
            }
            return Path.Combine(appData, "lacerte-mcp", "config.json");
        }

        // -----------------------------------------------------------------
        // Credential Manager P/Invoke (Windows only — duplicates the read
        // half of LacertMCP.Cli.Storage.AppIdStore).
        // -----------------------------------------------------------------

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential,
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [System.Runtime.InteropServices.DllImport("advapi32.dll",
            EntryPoint = "CredReadW",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [System.Runtime.InteropServices.DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr cred);

        private const uint CRED_TYPE_GENERIC = 1;

        private static bool TryCredRead(string target, out string value)
        {
            value = string.Empty;
            IntPtr credPtr = IntPtr.Zero;
            byte[]? bytes = null;
            try
            {
                if (!CredRead(target, CRED_TYPE_GENERIC, 0, out credPtr))
                {
                    return false;
                }
                var cred = (CREDENTIAL)System.Runtime.InteropServices.Marshal.PtrToStructure(credPtr, typeof(CREDENTIAL))!;
                if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                {
                    value = string.Empty;
                    return true;
                }
                bytes = new byte[cred.CredentialBlobSize];
                System.Runtime.InteropServices.Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                value = Encoding.Unicode.GetString(bytes);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                // P15-076: zero the managed byte[] before letting GC reclaim it so the
                // AppID plaintext doesn't linger on the heap until next compacting GC.
                // Mirrors the same fix in LacertMCP.Cli.Storage.AppIdStore.TryCredRead.
                // (The managed `value` string still survives — accepted residual exposure
                // per DECISIONS.md; AppID is re-mintable via `register --reset` / `unregister`.)
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                if (credPtr != IntPtr.Zero)
                {
                    CredFree(credPtr);
                }
            }
        }

        private static bool IsWindows()
            => Environment.OSVersion.Platform == PlatformID.Win32NT;
    }
}
