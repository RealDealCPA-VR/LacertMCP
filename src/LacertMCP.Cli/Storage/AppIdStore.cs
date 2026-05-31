using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LacertMCP.Cli.Storage
{
    /// <summary>
    /// Persistence for the per-install Lacerte SDK AppID GUID.
    ///
    /// <para>
    /// Per <c>DECISIONS.md</c> 2026-05-30 "AppID GUID in Windows Credential Manager":
    /// the primary backend on Windows is the Credential Manager (P/Invoke to
    /// <c>advapi32.dll</c> — <c>CredRead</c> / <c>CredWrite</c> / <c>CredDelete</c>),
    /// service name <c>lacerte-mcp</c>, account <c>app-id</c>. Off-Windows or when
    /// Credential Manager is unavailable, the GUID falls back to a JSON file at
    /// <c>%APPDATA%/lacerte-mcp/config.json</c> (POSIX <c>~/.config/lacerte-mcp/config.json</c>),
    /// constrained to the owning user.
    /// </para>
    ///
    /// <para>
    /// Picking the right backend at runtime keeps the CLI runnable off-Windows so the
    /// rest of the operator surface (status / field-access / doctor) is iterable without
    /// a Lacerte install. Credential Manager P/Invoke is preferred over the
    /// <c>CredentialManagement</c> NuGet because the latter drags in a couple of
    /// transitive dependencies for what is ultimately three exported functions.
    /// </para>
    /// </summary>
    public static class AppIdStore
    {
        // CredMan target/account constants — also surfaced for tests / status command.
        public const string CredentialTarget = "lacerte-mcp";
        public const string CredentialAccount = "app-id";

        /// <summary>
        /// Canonical AppID masking convention shared by every CLI command that may print one
        /// without an explicit <c>--show-app-id</c> opt-in (status, login, doctor). Shows the
        /// first 8 chars only — enough to disambiguate two installs without leaking the full
        /// GUID. Centralising here keeps masking consistent across commands so the
        /// "AppID never leaks in a non-opt-in channel" invariant has a single audit point.
        /// </summary>
        public static string Mask(Guid id)
        {
            var s = id.ToString("D");
            return s.Substring(0, 8) + "-****-****-****-************";
        }

        // CRED_TYPE_GENERIC = 1; CRED_PERSIST_LOCAL_MACHINE = 2.
        private const uint CRED_TYPE_GENERIC = 1;
        private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
        private const int ERROR_NOT_FOUND = 1168;

        /// <summary>
        /// Whether <see cref="Get"/> and <see cref="Set"/> use the Windows Credential Manager backend on this OS.
        /// Honors the test-only <c>LACERTE_MCP_FORCE_FILE_STORE=1</c> env var so the file-fallback can be
        /// exercised on Windows without touching the operator's real Credential Manager hive.
        /// </summary>
        public static bool UsesCredentialManager
        {
            get
            {
                if (Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE") == "1") return false;
                return Environment.OSVersion.Platform == PlatformID.Win32NT;
            }
        }

        /// <summary>
        /// Filesystem location used when not on Windows (or when CredMan is unavailable).
        /// Honors the <c>APPDATA</c> env var explicitly (Windows' <c>SpecialFolder.ApplicationData</c>
        /// resolves via Win32 and ignores per-process env overrides — but tests need that override).
        /// </summary>
        public static string FallbackConfigPath
        {
            get
            {
                // 1. Explicit env override (lets tests redirect without touching real APPDATA).
                var appData = Environment.GetEnvironmentVariable("APPDATA");

                // 2. SpecialFolder.ApplicationData (Windows: cached; POSIX: may be empty).
                if (string.IsNullOrEmpty(appData))
                {
                    appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                }

                // 3. POSIX XDG-ish fallback.
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
        }

        /// <summary>Read the persisted AppID. Returns <c>null</c> if none has been minted yet.</summary>
        public static Guid? Get()
        {
            if (UsesCredentialManager)
            {
                if (TryCredRead(CredentialTarget, out var raw))
                {
                    if (Guid.TryParse(raw, out var g)) return g;
                }
                return null;
            }
            return ReadFromFile();
        }

        /// <summary>
        /// P15-031: Tri-state diagnostic accessor that distinguishes "no AppID persisted"
        /// from "AppID persisted but the stored value is not a valid GUID". The plain
        /// <see cref="Get"/> collapses both into <c>null</c>, which hides the corruption
        /// case from the doctor probe.
        /// </summary>
        public enum AppIdDiagnostic { Absent, ParseFailed, Ok }

        /// <summary>
        /// Reads the raw stored AppID and reports whether it's absent, present but
        /// unparseable, or a valid GUID. Returns the parsed GUID via the out param
        /// only when <see cref="AppIdDiagnostic.Ok"/>; <paramref name="rawValue"/>
        /// carries the unparseable string when <see cref="AppIdDiagnostic.ParseFailed"/>.
        /// </summary>
        public static AppIdDiagnostic GetDiagnostic(out Guid value, out string? rawValue)
        {
            value = Guid.Empty;
            rawValue = null;
            string? raw;
            if (UsesCredentialManager)
            {
                if (!TryCredRead(CredentialTarget, out var credRaw))
                {
                    return AppIdDiagnostic.Absent;
                }
                if (string.IsNullOrEmpty(credRaw))
                {
                    return AppIdDiagnostic.Absent;
                }
                raw = credRaw;
            }
            else
            {
                raw = ReadRawFromFile();
                if (raw == null) return AppIdDiagnostic.Absent;
            }

            if (Guid.TryParse(raw, out var g))
            {
                value = g;
                return AppIdDiagnostic.Ok;
            }
            rawValue = raw;
            return AppIdDiagnostic.ParseFailed;
        }

        /// <summary>
        /// Raw read from the file backend — returns the stored AppId string without
        /// the <see cref="Guid.TryParse"/> collapse so the diagnostic accessor can
        /// distinguish "file missing/no key" from "key present but malformed".
        /// </summary>
        private static string? ReadRawFromFile()
        {
            var path = FallbackConfigPath;
            if (!File.Exists(path)) return null;
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<ConfigShape>(json);
                return cfg?.AppId;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Persist (or replace) the AppID.</summary>
        public static void Set(Guid id)
        {
            if (UsesCredentialManager)
            {
                CredWriteOrThrow(CredentialTarget, CredentialAccount, id.ToString("D"));
                return;
            }
            WriteToFile(id);
        }

        /// <summary>Forget the persisted AppID. Idempotent — never throws on "already missing".</summary>
        public static void Reset()
        {
            if (UsesCredentialManager)
            {
                CredDeleteIfPresent(CredentialTarget);
                return;
            }
            try
            {
                if (File.Exists(FallbackConfigPath))
                {
                    File.Delete(FallbackConfigPath);
                }
            }
            catch (IOException)
            {
                // Best-effort delete; surface nothing if the file vanished between Exists + Delete.
            }
        }

        // ---- File-backed fallback ---------------------------------------------------

        private sealed class ConfigShape
        {
            public string? AppId { get; set; }
        }

        private static Guid? ReadFromFile()
        {
            var path = FallbackConfigPath;
            if (!File.Exists(path)) return null;
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<ConfigShape>(json);
                if (cfg?.AppId == null) return null;
                if (Guid.TryParse(cfg.AppId, out var g)) return g;
                return null;
            }
            catch (Exception)
            {
                // Corrupt config — treat as unset; caller can re-mint via `register --reset`.
                return null;
            }
        }

        private static void WriteToFile(Guid id)
        {
            var path = FallbackConfigPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var cfg = new ConfigShape { AppId = id.ToString("D") };
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Best-effort: scope to owning user on POSIX. On Windows this method is
            // unreachable (CredMan path wins), so we don't need ACL hardening here.
            // On *nix we'd want chmod 0600 — out of scope for net48's stdlib;
            // file lives under user-owned $HOME which is already user-private by default.
        }

        // ---- Credential Manager P/Invoke -------------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL userCredential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr cred);

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
                var cred = (CREDENTIAL)Marshal.PtrToStructure(credPtr, typeof(CREDENTIAL))!;
                if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                {
                    value = string.Empty;
                    return true;
                }
                // Stored as UTF-16 (Unicode) — matches the byte count we used on Write.
                bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                value = Encoding.Unicode.GetString(bytes);
                return true;
            }
            finally
            {
                // P15-076: zero the managed byte[] before letting GC reclaim it so the
                // AppID plaintext doesn't linger on the heap until next compacting GC.
                // (The managed `value` string still survives — accepted residual exposure
                // per DECISIONS.md; AppID is re-mintable via `register --reset` / `unregister`.)
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                if (credPtr != IntPtr.Zero)
                {
                    CredFree(credPtr);
                }
            }
        }

        private static void CredWriteOrThrow(string target, string account, string value)
        {
            var blob = Encoding.Unicode.GetBytes(value);
            var blobPtr = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
                var cred = new CREDENTIAL
                {
                    Flags = 0,
                    Type = CRED_TYPE_GENERIC,
                    TargetName = target,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = blobPtr,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    AttributeCount = 0,
                    Attributes = IntPtr.Zero,
                    UserName = account,
                    Comment = "Lacerte MCP Application ID GUID — minted by `lacerte-mcp register`.",
                };
                if (!CredWrite(ref cred, 0))
                {
                    var err = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        $"CredWrite failed with Win32 error {err}. Target='{target}', Account='{account}'.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(blobPtr);
                // P15-076: zero the managed staging blob (the unmanaged HGlobal copy is
                // already freed; the managed byte[] would otherwise linger on the heap).
                Array.Clear(blob, 0, blob.Length);
            }
        }

        private static void CredDeleteIfPresent(string target)
        {
            if (!CredDelete(target, CRED_TYPE_GENERIC, 0))
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_NOT_FOUND) return;
                throw new InvalidOperationException($"CredDelete failed with Win32 error {err}. Target='{target}'.");
            }
        }
    }
}
