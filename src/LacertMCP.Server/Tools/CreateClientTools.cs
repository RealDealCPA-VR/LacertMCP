using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// Phase 7 tasks #57-59: <c>lacerte_client_create</c>. Wraps
    /// <see cref="LacerteSessionManager.CreateClient"/> with the client-side
    /// validation gates that mirror the SDK's pre-check behavior per agent 06
    /// §6, so failures land on the tool side (with structured messages)
    /// instead of as raw SDK status codes.
    ///
    /// <para>
    /// Validation order (matches the sim store's <c>SimulateCreateClient</c>
    /// for parity):
    /// </para>
    /// <list type="number">
    ///   <item><description>ClientID present, &lt;= 8 chars, matches <c>^[A-Z0-9]+$</c>.</description></item>
    ///   <item><description>TaxYear &gt;= 2018 (delegated to context-bound year).</description></item>
    ///   <item><description>TaxType non-empty.</description></item>
    /// </list>
    /// </summary>
    [McpServerToolType]
    public class CreateClientTools
    {
        private static readonly Regex _rxClientId = new Regex(
            @"^[A-Z0-9]+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Per-module accepted TaxType strings. Mirrors SimulationStore's
        // _moduleTaxTypes table — kept here so the tool can pre-flight the
        // mismatch before reaching the session (the live SDK also enforces
        // this server-side; we catch it earlier with a clearer message).
        private static readonly System.Collections.Generic.IReadOnlyDictionary<char, string[]> _moduleTaxTypes =
            new System.Collections.Generic.Dictionary<char, string[]>
            {
                { 'I', new[] { "Individual", "1040" } },
                { 'C', new[] { "Corporate", "1120" } },
                { 'P', new[] { "Partnership", "1065" } },
                { 'S', new[] { "SCorporate", "1120S", "S-Corporation", "SCorp" } },
                { 'F', new[] { "Fiduciary", "1041" } },
                { 'R', new[] { "Exempt", "990" } },
                { 'T', new[] { "Estate", "706" } },
                { 'N', new[] { "Gift", "709" } },
                { 'B', new[] { "Benefit", "5500" } },
            };

        private readonly LacerteSessionManager _session;

        public CreateClientTools(LacerteSessionManager session)
        {
            _session = session;
        }

        [McpServerTool]
        [Description("Create a new client in the active datapath/year/module. ClientID must be <= 8 alphanumeric chars. TaxType must match the module (e.g. 'Individual' or '1040' for module I). Returns the freshly-minted ClientNumber on success.")]
        public Task<string> LacerteClientCreate(
            [Description("Operator-chosen ClientID. <= 8 chars, alphanumeric only.")] string clientID,
            [Description("TaxType string passed to the SDK's IClient constructor. Must match the active module (e.g. 'Individual' / '1040' for module I).")] string taxType,
            [Description("First name (individual) OR entity-name override. Optional.")] string? firstName = null,
            [Description("Last name (individual). Optional.")] string? lastName = null,
            [Description("EmailID. Optional.")] string? emailID = null,
            [Description("If true, validate + simulate the create then roll back without persisting.")] bool dryRun = false,
            [Description("Optional idempotency key. Same key + same payload returns the cached result; same key + different payload throws 9102.")] string? idempotencyKey = null)
        {
            return WrapToolHandler.Run(async () =>
            {
                // -----------------------------------------------------------
                // Client-side validation — runs BEFORE the session call so
                // the failure surface matches the SDK pre-check behavior.
                // Throwing here means the synthetic 9xxx codes are surfaced
                // instead of letting the SDK return LengthExceeded (13) or
                // InValidClientName (24) with less specific context.
                // -----------------------------------------------------------

                if (string.IsNullOrEmpty(clientID))
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.ClientIDTooLong,
                        new Dictionary<string, object?>
                        {
                            ["clientID"] = clientID,
                            ["reason"] = "LengthExceeded — ClientID is required and must be 1-8 alphanumeric chars.",
                        });
                }

                if (clientID.Length > 8)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.ClientIDTooLong,
                        new Dictionary<string, object?>
                        {
                            ["clientID"] = clientID,
                            ["length"] = clientID.Length,
                            ["limit"] = 8,
                            ["reason"] = "LengthExceeded — ClientID exceeds the 8-character limit.",
                        });
                }

                if (!_rxClientId.IsMatch(clientID))
                {
                    throw new LacerteSDKError(
                        StatusCodes.InValidClientName,
                        new Dictionary<string, object?>
                        {
                            ["clientID"] = clientID,
                            ["reason"] = "ClientID must match ^[A-Z0-9]+$ — letters and digits only.",
                        });
                }

                if (string.IsNullOrWhiteSpace(taxType))
                {
                    throw new LacerteSDKError(
                        StatusCodes.TaxModuleMismatch,
                        new Dictionary<string, object?>
                        {
                            ["taxType"] = taxType,
                            ["reason"] = "TaxType is required (e.g. 'Individual', '1040').",
                        });
                }

                // TaxType must match the active module per agent 06 §6. The
                // session manager's ApplySimCreateClient does not enforce this
                // (it just appends to the active module store) — so the gate
                // lives here in the tool layer. The live SDK enforces server-
                // side; we just surface the error earlier with the right name.
                if (_session.Context != null
                    && _moduleTaxTypes.TryGetValue(_session.Context.Module, out var accepted))
                {
                    bool ok = false;
                    foreach (var a in accepted)
                    {
                        if (string.Equals(a, taxType, System.StringComparison.OrdinalIgnoreCase))
                        {
                            ok = true;
                            break;
                        }
                    }
                    if (!ok)
                    {
                        throw new LacerteSDKError(
                            StatusCodes.TaxModuleMismatch,
                            new Dictionary<string, object?>
                            {
                                ["taxType"] = taxType,
                                ["module"] = _session.Context.Module.ToString(),
                                ["acceptedTaxTypes"] = accepted,
                                ["reason"] = $"TaxType '{taxType}' is not valid for module '{_session.Context.Module}'.",
                            });
                    }
                }

                // Year guard fires inside session.CreateClient via context;
                // also defensively check here against the context if set so
                // we throw the right synthetic 9003 with a clear message
                // before the session-layer check fires.
                if (_session.Context != null && _session.Context.Year < 2018)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.TaxYearTooOld,
                        new Dictionary<string, object?>
                        {
                            ["taxYear"] = _session.Context.Year,
                            ["floor"] = 2018,
                        });
                }

                var args = new CreateClientArgs(
                    ClientID: clientID,
                    TaxType: taxType,
                    FirstName: firstName,
                    LastName: lastName,
                    EmailID: emailID);

                var result = await _session.CreateClient(args, dryRun, idempotencyKey);

                // The session returns CreateClientResult { Status, ClientNumber? }.
                // Translate non-Success into a structured error so the tool
                // never returns success:true with a failure TAPIStatus.
                if (result.Status != StatusCodes.Success)
                {
                    throw new LacerteSDKError(
                        result.Status,
                        new Dictionary<string, object?>
                        {
                            ["clientID"] = clientID,
                            ["taxType"] = taxType,
                        });
                }

                var info = StatusCodes.GetStatusInfo(result.Status);
                return (object)new
                {
                    statusCode = result.Status,
                    statusName = info.Name,
                    clientNumber = result.ClientNumber,
                    clientID,
                    dryRun,
                };
            });
        }
    }
}
