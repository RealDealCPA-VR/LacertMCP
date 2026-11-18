using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Odbc;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LacertMCP.Server.Odbc
{
    // =======================================================================
    // FP-2 — out-of-process detail-row INSERT.
    //
    // The INSERT never runs inside the long-lived MCP server: an ODBC driver
    // crash on INSERT (reported for the 32-bit LacerteDSIIDriver) would take
    // down every agent's connection and the in-memory Intuit sign-in. The
    // server spawns its own exe with --detail-write-worker, hands it ONE
    // request on stdin, reads ONE result line from stdout, and treats a
    // crash / hang / garbled output as a clean failure.
    //
    // JSON is (de)serialized with DataContractJsonSerializer (in-box on
    // net48) so the worker loads as few assemblies as possible before it
    // touches the driver.
    // =======================================================================

    /// <summary>Request the server writes to the worker's stdin.</summary>
    [DataContract]
    public sealed class DetailWriteRequest
    {
        [DataMember(Name = "connectionString", Order = 1)] public string? ConnectionString { get; set; }
        [DataMember(Name = "sql", Order = 2)] public string? Sql { get; set; }
    }

    /// <summary>Result line the worker writes to stdout.</summary>
    [DataContract]
    public sealed class DetailWriteResponse
    {
        [DataMember(Name = "ok", Order = 1)] public bool Ok { get; set; }
        [DataMember(Name = "rowsAffected", Order = 2, EmitDefaultValue = true)] public int? RowsAffected { get; set; }
        [DataMember(Name = "error", Order = 3, EmitDefaultValue = true)] public string? Error { get; set; }
        [DataMember(Name = "sqlState", Order = 4, EmitDefaultValue = true)] public string? SqlState { get; set; }
        [DataMember(Name = "errorType", Order = 5, EmitDefaultValue = true)] public string? ErrorType { get; set; }
    }

    /// <summary>How a worker run ended, from the server's side.</summary>
    public enum DetailWriteRunStatus
    {
        /// <summary>The worker ran the INSERT and reported success.</summary>
        Ok,
        /// <summary>The worker ran and reported an error (driver/SQL error, bad request). It exited cleanly.</summary>
        Failed,
        /// <summary>The worker exited without a result line (driver crash, unhandled fault, garbled stdout).</summary>
        Crashed,
        /// <summary>The worker did not finish within the timeout and was killed.</summary>
        TimedOut,
        /// <summary>The worker process could not be started (nothing ran).</summary>
        NotStarted,
    }

    /// <summary>Outcome of one worker run.</summary>
    public sealed class DetailWriteRunResult
    {
        public DetailWriteRunStatus Status { get; set; }
        public int? RowsAffected { get; set; }
        public string? Error { get; set; }
        public string? SqlState { get; set; }
        public string? ErrorType { get; set; }
        public int? ExitCode { get; set; }
        /// <summary>Tail of the worker's stderr (diagnostics only; may be empty).</summary>
        public string? Stderr { get; set; }
    }

    /// <summary>
    /// Seam for running one INSERT out of process. Production:
    /// <see cref="ProcessDetailWriteRunner"/>. Tests inject a fake so no
    /// process is ever spawned.
    /// </summary>
    public interface IDetailWriteRunner
    {
        DetailWriteRunResult Run(string connectionString, string sql, TimeSpan timeout);
    }

    /// <summary>
    /// Spawns <c>LacertMCP.Server.exe --detail-write-worker</c> (this assembly)
    /// with redirected streams and no window, writes the request, and maps the
    /// exit to a <see cref="DetailWriteRunResult"/>. Never throws for a worker
    /// failure — every failure becomes a status.
    /// </summary>
    public sealed class ProcessDetailWriteRunner : IDetailWriteRunner
    {
        private readonly string _exePath;

        public ProcessDetailWriteRunner() : this(DefaultExePath()) { }

        public ProcessDetailWriteRunner(string exePath)
        {
            _exePath = exePath ?? throw new ArgumentNullException(nameof(exePath));
        }

        public string ExePath => _exePath;

        /// <summary>
        /// Path of the server exe. Prefers the original location over a test
        /// runner's shadow copy (CodeBase), falling back to Location.
        /// </summary>
        public static string DefaultExePath()
        {
            var asm = typeof(ProcessDetailWriteRunner).Assembly;
            try
            {
#pragma warning disable SYSLIB0012 // CodeBase is the right API on net48 (shadow copies)
                var codeBase = asm.CodeBase;
#pragma warning restore SYSLIB0012
                if (!string.IsNullOrEmpty(codeBase))
                {
                    var local = new Uri(codeBase).LocalPath;
                    if (File.Exists(local)) return local;
                }
            }
            catch (Exception)
            {
                // fall through to Location
            }
            return asm.Location;
        }

        public DetailWriteRunResult Run(string connectionString, string sql, TimeSpan timeout)
        {
            if (!File.Exists(_exePath))
            {
                return new DetailWriteRunResult
                {
                    Status = DetailWriteRunStatus.NotStarted,
                    Error = "Worker executable not found: " + _exePath,
                };
            }

            var psi = new ProcessStartInfo(_exePath, DetailWriteWorker.WorkerSwitch)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(_exePath) ?? Environment.CurrentDirectory,
            };

            Process? proc = null;
            try
            {
                try
                {
                    proc = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    return new DetailWriteRunResult
                    {
                        Status = DetailWriteRunStatus.NotStarted,
                        Error = ex.GetType().Name + ": " + ex.Message,
                    };
                }
                if (proc == null)
                {
                    return new DetailWriteRunResult { Status = DetailWriteRunStatus.NotStarted, Error = "Process.Start returned null." };
                }

                // Drain both pipes concurrently so a chatty stderr can't block the child.
                // The worker writes exactly one result line. Read just that line, so a
                // grandchild the driver may start (inheriting the pipe) can't hold
                // the read open and turn a successful INSERT into a "crash".
                Task<string> stdoutTask = proc.StandardOutput.ReadLineAsync();
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

                try
                {
                    var payload = DetailWriteWorker.Serialize(new DetailWriteRequest { ConnectionString = connectionString, Sql = sql });
                    var stdin = proc.StandardInput.BaseStream;
                    stdin.Write(payload, 0, payload.Length);
                    stdin.Flush();
                    proc.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The child died before reading stdin — the exit status
                    // below reports it as a crash.
                }

                bool exited = proc.WaitForExit((int)Math.Max(1, Math.Min(int.MaxValue, timeout.TotalMilliseconds)));
                if (!exited)
                {
                    try { proc.Kill(); } catch (Exception) { /* already gone */ }
                    try { proc.WaitForExit(5000); } catch (Exception) { /* best effort */ }
                    return new DetailWriteRunResult
                    {
                        Status = DetailWriteRunStatus.TimedOut,
                        Error = "The worker did not finish within " + timeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s and was stopped.",
                        Stderr = Tail(SafeResult(stderrTask, 2000)),
                    };
                }
                proc.WaitForExit(); // flush async readers

                var stdout = SafeResult(stdoutTask, 5000);
                var stderr = Tail(SafeResult(stderrTask, 5000));
                int exitCode = proc.ExitCode;
                var response = DetailWriteWorker.ParseResponse(stdout);
                if (response == null)
                {
                    return new DetailWriteRunResult
                    {
                        Status = DetailWriteRunStatus.Crashed,
                        ExitCode = exitCode,
                        Error = "The worker exited (code " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") without a result.",
                        Stderr = stderr,
                    };
                }
                return new DetailWriteRunResult
                {
                    Status = response.Ok ? DetailWriteRunStatus.Ok : DetailWriteRunStatus.Failed,
                    RowsAffected = response.RowsAffected,
                    Error = response.Error,
                    SqlState = response.SqlState,
                    ErrorType = response.ErrorType,
                    ExitCode = exitCode,
                    Stderr = stderr,
                };
            }
            finally
            {
                proc?.Dispose();
            }
        }

        private static string SafeResult(Task<string> t, int waitMs)
        {
            try
            {
                return t.Wait(waitMs) ? (t.Result ?? string.Empty) : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string Tail(string s)
        {
            s = (s ?? string.Empty).Trim();
            return s.Length <= 500 ? s : s.Substring(s.Length - 500);
        }
    }

    /// <summary>
    /// The worker side: <c>LacertMCP.Server.exe --detail-write-worker</c>.
    /// Reads one <see cref="DetailWriteRequest"/> from stdin, runs it with
    /// <c>ExecuteNonQuery</c>, writes one <see cref="DetailWriteResponse"/>
    /// line to stdout and returns the exit code. Starts nothing else (no MCP
    /// host, no logging, no session).
    /// </summary>
    public static class DetailWriteWorker
    {
        public const string WorkerSwitch = "--detail-write-worker";

        /// <summary>Exit code: INSERT ran.</summary>
        public const int ExitOk = 0;
        /// <summary>Exit code: the driver / SQL reported an error (result line written).</summary>
        public const int ExitFailed = 1;
        /// <summary>Exit code: the request was unreadable or not a detail INSERT (result line written).</summary>
        public const int ExitBadRequest = 2;

        // Only a single-row detail INSERT built by DetailRowWriter is accepted,
        // so the worker can't be used as a general SQL runner.
        private static readonly Regex AllowedSql = new Regex(
            "^INSERT INTO \\[[A-Z0-9]{1,8}-Detail\\] \\([A-Za-z, ]+\\) VALUES \\(",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// True when <paramref name="sql"/> is one statement ending in ")": no
        /// ";" or comment marker outside a quoted string literal ('' escapes a quote).
        /// </summary>
        internal static bool IsSingleStatement(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return false;
            bool inQuote = false;
            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                if (c == '\'')
                {
                    if (inQuote && i + 1 < sql.Length && sql[i + 1] == '\'') { i++; continue; }
                    inQuote = !inQuote;
                    continue;
                }
                if (inQuote) continue;
                if (c == ';') return false;
                if ((c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') || (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')) return false;
            }
            return !inQuote && sql.TrimEnd().EndsWith(")", StringComparison.Ordinal);
        }

        public static bool IsWorkerInvocation(string[]? args)
            => args != null && args.Length > 0 && string.Equals(args[0], WorkerSwitch, StringComparison.Ordinal);

        /// <summary>Process entry: real stdin/stdout and the real ODBC driver.</summary>
        public static int RunMain()
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            return Run(stdin, stdout, cs => new OdbcConnection(cs));
        }

        /// <summary>Testable core.</summary>
        public static int Run(Stream input, Stream output, Func<string, DbConnection> connectionFactory)
        {
            DetailWriteResponse response;
            int exit;
            DetailWriteRequest? request = null;
            try
            {
                using var ms = new MemoryStream();
                input.CopyTo(ms);
                ms.Position = 0;
                request = (DetailWriteRequest?)new DataContractJsonSerializer(typeof(DetailWriteRequest)).ReadObject(ms);
            }
            catch (Exception ex)
            {
                response = new DetailWriteResponse { Ok = false, Error = "Unreadable request: " + ex.Message, ErrorType = ex.GetType().Name };
                Write(output, response);
                return ExitBadRequest;
            }

            if (request == null || string.IsNullOrWhiteSpace(request.ConnectionString) || string.IsNullOrEmpty(request.Sql))
            {
                Write(output, new DetailWriteResponse { Ok = false, Error = "Request must carry connectionString and sql.", ErrorType = "BadRequest" });
                return ExitBadRequest;
            }
            if (!AllowedSql.IsMatch(request.Sql) || !IsSingleStatement(request.Sql!))
            {
                Write(output, new DetailWriteResponse { Ok = false, Error = "Only a detail-row INSERT is accepted.", ErrorType = "BadRequest" });
                return ExitBadRequest;
            }

            try
            {
                using var conn = connectionFactory(request.ConnectionString!);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = request.Sql;
                int n = cmd.ExecuteNonQuery();
                response = new DetailWriteResponse { Ok = true, RowsAffected = n };
                exit = ExitOk;
            }
            catch (OdbcException ex)
            {
                string? state = null;
                string message = ex.Message;
                try
                {
                    if (ex.Errors != null && ex.Errors.Count > 0)
                    {
                        state = ex.Errors[0]?.SQLState;
                        message = ex.Errors[0]?.Message ?? ex.Message;
                    }
                }
                catch (Exception) { /* keep ex.Message */ }
                response = new DetailWriteResponse { Ok = false, Error = message, SqlState = state, ErrorType = ex.GetType().Name };
                exit = ExitFailed;
            }
            catch (Exception ex)
            {
                response = new DetailWriteResponse { Ok = false, Error = ex.Message, ErrorType = ex.GetType().Name };
                exit = ExitFailed;
            }
            Write(output, response);
            return exit;
        }

        internal static byte[] Serialize<T>(T value)
        {
            using var ms = new MemoryStream();
            new DataContractJsonSerializer(typeof(T)).WriteObject(ms, value);
            return ms.ToArray();
        }

        private static void Write(Stream output, DetailWriteResponse response)
        {
            var bytes = Serialize(response);
            output.Write(bytes, 0, bytes.Length);
            output.WriteByte((byte)'\n');
            output.Flush();
        }

        /// <summary>
        /// The last stdout line that parses as a result object, or null. Lines
        /// before it (anything a driver might print) are ignored.
        /// </summary>
        public static DetailWriteResponse? ParseResponse(string? stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout)) return null;
            var lines = stdout!.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (!line.StartsWith("{", StringComparison.Ordinal) || line.IndexOf("\"ok\"", StringComparison.Ordinal) < 0) continue;
                try
                {
                    using var ms = new MemoryStream(Encoding.UTF8.GetBytes(line));
                    var r = new DataContractJsonSerializer(typeof(DetailWriteResponse)).ReadObject(ms) as DetailWriteResponse;
                    if (r != null) return r;
                }
                catch (Exception)
                {
                    // not a result line
                }
            }
            return null;
        }
    }
}
