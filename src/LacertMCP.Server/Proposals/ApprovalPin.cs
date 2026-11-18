using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LacertMCP.Server.Proposals
{
    /// <summary>Result of an approval-PIN check.</summary>
    public enum ApprovalPinOutcome
    {
        /// <summary>The PIN is right.</summary>
        Ok,
        /// <summary>No PIN is set: approvals are impossible until the owner sets one (<c>lacerte-mcp approval-pin set</c>).</summary>
        NotSet,
        /// <summary>Wrong PIN; <see cref="ApprovalPinCheck.RemainingAttempts"/> more before the lockout.</summary>
        Wrong,
        /// <summary>Too many wrong PINs: refused until <see cref="ApprovalPinCheck.LockedUntilUtc"/>.</summary>
        Locked,
    }

    /// <summary>What <see cref="ApprovalPin.Verify"/> (or the current-PIN check of <see cref="ApprovalPin.Set"/>) found. Never carries the PIN.</summary>
    public sealed class ApprovalPinCheck
    {
        public ApprovalPinOutcome Outcome { get; set; }
        /// <summary>Wrong: attempts left before the lockout (0 when this attempt locked it).</summary>
        public int RemainingAttempts { get; set; }
        /// <summary>Locked: when approvals open again (UTC).</summary>
        public DateTimeOffset? LockedUntilUtc { get; set; }

        public bool IsOk => Outcome == ApprovalPinOutcome.Ok;
    }

    /// <summary>Public state of the PIN (no hash, no salt).</summary>
    public sealed class ApprovalPinStatus
    {
        public bool IsSet { get; set; }
        /// <summary>First time a PIN was set on this PC (UTC); null when never.</summary>
        public DateTimeOffset? SetAtUtc { get; set; }
        /// <summary>Last set / change / reset (UTC). The control page shows it so the owner notices an unexpected change.</summary>
        public DateTimeOffset? ChangedAtUtc { get; set; }
        /// <summary>Set while approvals are locked after too many wrong PINs.</summary>
        public DateTimeOffset? LockedUntilUtc { get; set; }
        /// <summary>Consecutive wrong PINs since the last right one (or the end of the last lockout).</summary>
        public int FailedAttempts { get; set; }
    }

    /// <summary>
    /// The approval PIN: proof that a PERSON approved a proposal (or resolved an
    /// Uncertain line), and their confirmation of the return it goes into. An AI
    /// agent with shell access on the Lacerte PC can call the approve endpoint or
    /// the CLI, but it doesn't know the PIN.
    ///
    /// <para><b>Storage.</b> <c>approval-pin.json</c> in the data folder (the
    /// parent of the <c>proposals</c> folder: <c>LACERTE_HTTP_DATA_DIR</c>, else
    /// <c>%APPDATA%\lacerte-mcp</c>), shared by the server and the CLI and re-read
    /// on every check. PBKDF2-SHA256, 16-byte random salt, 310,000 iterations,
    /// 32-byte hash; fixed-time comparison. The PIN itself is never stored, logged
    /// or echoed. Writes are atomic (temp file + replace) under an exclusive
    /// <c>approval-pin.lock</c> file, so the server and the CLI share the lockout
    /// counter without losing an update.</para>
    ///
    /// <para><b>Lockout.</b> <see cref="MaxFailedAttempts"/> consecutive wrong PINs
    /// lock approvals for <see cref="LockoutDuration"/>; a right PIN resets the
    /// counter, and so does the end of a lockout.</para>
    ///
    /// <para>No MCP tool sets, verifies, resets or reads it.</para>
    /// </summary>
    public sealed class ApprovalPin
    {
        public const string FileName = "approval-pin.json";
        public const int MinLength = 6;
        public const int MaxLength = 64;
        public const int MaxFailedAttempts = 5;
        public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

        internal const int DefaultIterations = 310_000;
        private const int SaltBytes = 16;
        private const int HashBytes = 32;
        private const string Algorithm = "PBKDF2-SHA256";

        /// <summary>The instruction shown wherever an approval needs a PIN and none is set.</summary>
        public const string SetupInstruction =
            "No approval PIN is set. On the Lacerte PC, open a console and run `lacerte-mcp approval-pin set`, then approve again.";

        private static readonly object s_gate = new object();

        private static readonly JsonSerializerOptions s_json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        public ApprovalPin(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("directory is required", nameof(directory));
            DirectoryPath = Path.GetFullPath(directory);
        }

        /// <summary>The folder holding <c>approval-pin.json</c>.</summary>
        public string DirectoryPath { get; }

        public string FilePath => Path.Combine(DirectoryPath, FileName);

        /// <summary>PBKDF2 iterations for a NEW hash (tests lower it). Verification uses the count stored with the hash.</summary>
        internal int Iterations { get; set; } = DefaultIterations;

        /// <summary>Clock seam for tests.</summary>
        internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

        /// <summary>How long a caller waits for another process's lock.</summary>
        internal TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// The data folder the PIN lives in: <c>LACERTE_HTTP_DATA_DIR</c>, else
        /// <c>%APPDATA%\lacerte-mcp</c> (the parent of <see cref="ProposalStore.ResolveDirectory"/>).
        /// </summary>
        public static string? ResolveDirectory(IDictionary<string, string?>? env = null)
        {
            var proposals = ProposalStore.ResolveDirectory(env);
            return proposals == null ? null : Path.GetDirectoryName(proposals);
        }

        /// <summary>The PIN that belongs with a proposal folder: the folder's parent (the data folder).</summary>
        public static ApprovalPin ForProposalDirectory(string proposalDirectory)
        {
            var full = Path.GetFullPath(proposalDirectory);
            var parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return new ApprovalPin(string.IsNullOrEmpty(parent) ? full : parent!);
        }

        // =================================================================
        // Rules
        // =================================================================

        /// <summary>Why <paramref name="pin"/> can't be used as a PIN, or null when it can. The message never contains the PIN.</summary>
        public static string? CheckRules(string? pin)
        {
            if (pin == null || pin.Length == 0) return "Enter a PIN.";
            var p = Normalize(pin);
            if (p.Length < MinLength) return "The PIN must be at least " + MinLength + " characters (digits or a passphrase).";
            if (p.Length > MaxLength) return "The PIN must be at most " + MaxLength + " characters.";
            if (p.Trim().Length == 0) return "The PIN can't be only spaces.";
            if (p.All(c => c == p[0])) return "The PIN can't be one character repeated.";
            if (p.All(c => c >= '0' && c <= '9'))
            {
                bool up = true, down = true;
                for (int i = 1; i < p.Length; i++)
                {
                    if (p[i] != (char)(p[i - 1] + 1)) up = false;
                    if (p[i] != (char)(p[i - 1] - 1)) down = false;
                }
                if (up || down) return "The PIN can't be a straight run of digits counting up or down.";
            }
            return null;
        }

        private static string Normalize(string pin) => pin.Normalize(NormalizationForm.FormC);

        // =================================================================
        // Operations
        // =================================================================

        /// <summary>Whether a PIN is set, when it last changed, and the lockout state.</summary>
        public ApprovalPinStatus Status()
        {
            var r = ReadRecord();
            var now = Clock();
            var locked = r?.LockedUntilUtc != null && r.LockedUntilUtc.Value > now ? r.LockedUntilUtc : null;
            return new ApprovalPinStatus
            {
                IsSet = r != null && HasHash(r),
                SetAtUtc = r?.SetAtUtc,
                ChangedAtUtc = r?.ChangedAtUtc,
                LockedUntilUtc = locked,
                FailedAttempts = r == null ? 0 : (r.LockedUntilUtc != null && r.LockedUntilUtc.Value <= now ? 0 : r.FailedAttempts),
            };
        }

        /// <summary>
        /// Checks <paramref name="pin"/>. A wrong PIN counts toward the lockout; a
        /// right one resets the counter. While locked every attempt is refused (and
        /// not counted) without checking the PIN.
        /// </summary>
        public ApprovalPinCheck Verify(string? pin)
        {
            using (AcquireLock())
            {
                var r = ReadRecord();
                return VerifyLocked(r, pin, persist: true);
            }
        }

        /// <summary>
        /// Sets a new PIN. When one is set, <paramref name="currentPin"/> must verify
        /// first (wrong / locked count like <see cref="Verify"/>, and nothing changes).
        /// Throws <see cref="ArgumentException"/> (message without the PIN) when the
        /// new PIN breaks the rules (<see cref="CheckRules"/>).
        /// </summary>
        public ApprovalPinCheck Set(string newPin, string? currentPin)
        {
            var problem = CheckRules(newPin);
            if (problem != null) throw new ArgumentException(problem, nameof(newPin));
            using (AcquireLock())
            {
                var r = ReadRecord();
                if (r != null && HasHash(r))
                {
                    var check = VerifyLocked(r, currentPin, persist: true);
                    if (!check.IsOk) return check;
                    r = ReadRecord();
                }
                var now = Clock();
                var salt = new byte[SaltBytes];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
                var iterations = Math.Max(1, Iterations);
                var hash = Derive(Normalize(newPin), salt, iterations);
                var next = new PinRecord
                {
                    Version = 1,
                    Algorithm = Algorithm,
                    Iterations = iterations,
                    Salt = Convert.ToBase64String(salt),
                    Hash = Convert.ToBase64String(hash),
                    SetAtUtc = r?.SetAtUtc ?? now,
                    ChangedAtUtc = now,
                    FailedAttempts = 0,
                    LockedUntilUtc = null,
                };
                WriteRecord(next);
                return new ApprovalPinCheck { Outcome = ApprovalPinOutcome.Ok };
            }
        }

        /// <summary>Clears the PIN (for a forgotten one). Approvals are impossible until a new PIN is set. The change time is kept.</summary>
        public void Reset()
        {
            using (AcquireLock())
            {
                var r = ReadRecord();
                WriteRecord(new PinRecord
                {
                    Version = 1,
                    Algorithm = Algorithm,
                    SetAtUtc = r?.SetAtUtc,
                    ChangedAtUtc = Clock(),
                });
            }
        }

        private ApprovalPinCheck VerifyLocked(PinRecord? r, string? pin, bool persist)
        {
            if (r == null || !HasHash(r)) return new ApprovalPinCheck { Outcome = ApprovalPinOutcome.NotSet };
            var now = Clock();
            if (r.LockedUntilUtc != null)
            {
                if (r.LockedUntilUtc.Value > now)
                    return new ApprovalPinCheck { Outcome = ApprovalPinOutcome.Locked, LockedUntilUtc = r.LockedUntilUtc };
                // The lockout is over: a fresh set of attempts.
                r.LockedUntilUtc = null;
                r.FailedAttempts = 0;
            }

            bool ok;
            try
            {
                var salt = Convert.FromBase64String(r.Salt!);
                var expected = Convert.FromBase64String(r.Hash!);
                ok = r.Iterations > 0 && expected.Length == HashBytes && !string.IsNullOrEmpty(pin)
                     && FixedTimeEquals(Derive(Normalize(pin!), salt, r.Iterations), expected);
            }
            catch (FormatException)
            {
                ok = false;
            }

            ApprovalPinCheck result;
            if (ok)
            {
                r.FailedAttempts = 0;
                r.LockedUntilUtc = null;
                result = new ApprovalPinCheck { Outcome = ApprovalPinOutcome.Ok };
            }
            else
            {
                r.FailedAttempts++;
                if (r.FailedAttempts >= MaxFailedAttempts)
                {
                    r.LockedUntilUtc = now + LockoutDuration;
                    result = new ApprovalPinCheck { Outcome = ApprovalPinOutcome.Locked, LockedUntilUtc = r.LockedUntilUtc, RemainingAttempts = 0 };
                }
                else
                {
                    result = new ApprovalPinCheck { Outcome = ApprovalPinOutcome.Wrong, RemainingAttempts = MaxFailedAttempts - r.FailedAttempts };
                }
            }
            if (persist) WriteRecord(r);
            return result;
        }

        /// <summary>A sentence for a refused check (no PIN in it).</summary>
        public static string Describe(ApprovalPinCheck check)
        {
            switch (check.Outcome)
            {
                case ApprovalPinOutcome.Ok:
                    return "The approval PIN is right.";
                case ApprovalPinOutcome.NotSet:
                    return SetupInstruction;
                case ApprovalPinOutcome.Wrong:
                    return "Wrong approval PIN. " + check.RemainingAttempts + " attempt" + (check.RemainingAttempts == 1 ? "" : "s")
                        + " left before approvals lock for " + (int)LockoutDuration.TotalMinutes + " minutes.";
                default:
                    var until = check.LockedUntilUtc;
                    return "Too many wrong approval PINs: approvals are locked"
                        + (until.HasValue
                            ? " until " + until.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                              + " (" + until.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC)"
                            : string.Empty)
                        + ". Try again after that.";
            }
        }

        // =================================================================
        // Crypto + file
        // =================================================================

        private static byte[] Derive(string pin, byte[] salt, int iterations)
        {
            var bytes = Encoding.UTF8.GetBytes(pin);
            try
            {
                using var kdf = new Rfc2898DeriveBytes(bytes, salt, iterations, HashAlgorithmName.SHA256);
                return kdf.GetBytes(HashBytes);
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static bool HasHash(PinRecord r) => !string.IsNullOrEmpty(r.Hash) && !string.IsNullOrEmpty(r.Salt);

        private PinRecord? ReadRecord()
        {
            var path = FilePath;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return JsonSerializer.Deserialize<PinRecord>(fs, s_json);
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(20);
                }
                catch (JsonException)
                {
                    // A damaged file is "set" in an unusable way: refuse approvals (NotSet would invite a silent re-set).
                    return new PinRecord { Hash = "invalid", Salt = "invalid", Iterations = 0 };
                }
            }
        }

        private void WriteRecord(PinRecord r)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = FilePath;
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(r, s_json), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>In-process monitor + exclusive lock file, held for one read-modify-write.</summary>
        private IDisposable AcquireLock()
        {
            Directory.CreateDirectory(DirectoryPath);
            var deadline = DateTime.UtcNow + LockTimeout;
            if (!Monitor.TryEnter(s_gate, LockTimeout)) throw Busy();
            try
            {
                var lockPath = Path.Combine(DirectoryPath, "approval-pin.lock");
                while (true)
                {
                    try
                    {
                        var fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                        return new LockHandle(fs);
                    }
                    catch (IOException) when (DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(25);
                    }
                    catch (IOException)
                    {
                        throw Busy();
                    }
                }
            }
            catch
            {
                Monitor.Exit(s_gate);
                throw;
            }
        }

        private static IOException Busy()
            => new IOException("Another process is checking the approval PIN right now (approval-pin.lock is held). Try again in a moment.");

        private sealed class LockHandle : IDisposable
        {
            private FileStream? _fs;
            public LockHandle(FileStream fs) { _fs = fs; }
            public void Dispose()
            {
                var fs = Interlocked.Exchange(ref _fs, null);
                if (fs == null) return;
                fs.Dispose();
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>The file's shape. Never holds the PIN.</summary>
        private sealed class PinRecord
        {
            public int Version { get; set; } = 1;
            public string? Algorithm { get; set; }
            public int Iterations { get; set; }
            public string? Salt { get; set; }
            public string? Hash { get; set; }
            public DateTimeOffset? SetAtUtc { get; set; }
            public DateTimeOffset? ChangedAtUtc { get; set; }
            public int FailedAttempts { get; set; }
            public DateTimeOffset? LockedUntilUtc { get; set; }
        }
    }
}
