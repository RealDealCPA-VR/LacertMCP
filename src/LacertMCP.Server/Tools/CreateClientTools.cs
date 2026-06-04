using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
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

        // Per-module accepted TaxType strings now live on
        // Schema.Modules.TaxTypeAliases — single source of truth shared
        // with SimulationStore (preserves observational identity between
        // sim and live per the two-mode discipline).

        private readonly LacerteSessionManager _session;

        public CreateClientTools(LacerteSessionManager session)
        {
            _session = session;
        }

        [McpServerTool]
        [Description("Create a new client in the active datapath/taxYear/module. ClientId must be <= 8 alphanumeric chars. TaxType must match the module (e.g. 'Individual' or '1040' for module I). FirstName, LastName and EmailId are required per spec §4 and REQUIREMENTS §Create. Returns the freshly-minted ClientNumber on success.")]
        public Task<string> LacerteClientCreate(
            // P16-054/P16-080 — canonical wire casing is camelCase with the
            // 2-letter Id suffix mixed-case (clientId / emailId / subClientId).
            // This matches the .NET Framework Design Guidelines and removes
            // the ALL-CAPS suffix anti-pattern that bled into JSON wire keys.
            [Description("Operator-chosen ClientId. <= 8 chars, alphanumeric only. Lowercase is accepted and normalized to uppercase before storage so the value passed to the live SDK matches the case-sensitive bridge pattern.")] string clientId,
            [Description("TaxType string passed to the SDK's IClient constructor. Must match the active module (e.g. 'Individual' / '1040' for module I).")] string taxType,
            [Description("First name (individual) OR entity-name override. Required per Lacerte spec §4 - pass an empty string only if you have explicit live-SDK evidence the field is dispensable for this module.")] string firstName,
            [Description("Last name (individual). Required per Lacerte spec §4 - pass an empty string only if you have explicit live-SDK evidence the field is dispensable for this module.")] string lastName,
            [Description("EmailId. Required per Lacerte spec §4 - pass an empty string only if you have explicit live-SDK evidence the field is dispensable for this module.")] string emailId,
            [Description("If true, validate + simulate the create then roll back without persisting.")] bool dryRun = false,
            [Description("Optional idempotency key. Same key + same payload returns the cached result for up to 24h after the first call; replays after the TTL re-execute (treated as a new key). Same key + different payload throws 9102.")] string? idempotencyKey = null)
        {
            return WrapToolHandler.Run(async () =>
            {
                // -----------------------------------------------------------
                // Client-side validation - runs BEFORE the session call so
                // the failure surface matches the SDK pre-check behavior.
                // Throwing here means the synthetic 9xxx codes are surfaced
                // instead of letting the SDK return LengthExceeded (13) or
                // InValidClientName (24) with less specific context.
                // -----------------------------------------------------------

                if (string.IsNullOrEmpty(clientId))
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.ClientIDTooLong,
                        new Dictionary<string, object?>
                        {
                            // P16-054/P16-080 — error-context echoes the
                            // operator-facing wire name `clientId`.
                            ["clientId"] = clientId,
                            ["reason"] = "LengthExceeded - clientId is required and must be 1-8 alphanumeric chars.",
                        });
                }

                // P16-038 — normalize case so lowercase operator-formatted input
                // (common from LLM-generated tool calls) doesn't slip past the
                // IgnoreCase regex on _rxClientId and then get late-rejected by
                // the case-sensitive ClientNumberPattern at the bridge boundary
                // (BridgeInputValidation.cs:36). Same normalization the sim
                // store needs (SimulationStore.SimulateCreateClient stores
                // ClientID verbatim today). Doing this before the length /
                // regex / dup checks keeps every downstream layer
                // observationally identical regardless of the operator's
                // typing convention.
                clientId = clientId.ToUpperInvariant();

                if (clientId.Length > 8)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.ClientIDTooLong,
                        new Dictionary<string, object?>
                        {
                            ["clientId"] = clientId,
                            ["length"] = clientId.Length,
                            ["limit"] = 8,
                            ["reason"] = "LengthExceeded - clientId exceeds the 8-character limit.",
                        });
                }

                if (!_rxClientId.IsMatch(clientId))
                {
                    throw new LacerteSDKError(
                        StatusCodes.InValidClientName,
                        new Dictionary<string, object?>
                        {
                            ["clientId"] = clientId,
                            ["reason"] = "clientId must match ^[A-Z0-9]+$ - letters and digits only.",
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

                // Per Lacerte_MCP_Structure_Spec.md §4 and REQUIREMENTS.md §Create,
                // FirstName, LastName, and EmailId are part of the minimum-required
                // field set for lacerte_client_create. Enforce here so the failure
                // surfaces as a structured InValidClientName (24) BEFORE the
                // session call rather than as a late SDK rejection with less
                // actionable context.
                if (string.IsNullOrWhiteSpace(firstName))
                {
                    throw new LacerteSDKError(
                        StatusCodes.InValidClientName,
                        new Dictionary<string, object?>
                        {
                            ["field"] = "firstName",
                            ["reason"] = "FirstName is required per spec §4 / REQUIREMENTS §Create.",
                        });
                }

                if (string.IsNullOrWhiteSpace(lastName))
                {
                    throw new LacerteSDKError(
                        StatusCodes.InValidClientName,
                        new Dictionary<string, object?>
                        {
                            ["field"] = "lastName",
                            ["reason"] = "LastName is required per spec §4 / REQUIREMENTS §Create.",
                        });
                }

                if (string.IsNullOrWhiteSpace(emailId))
                {
                    throw new LacerteSDKError(
                        StatusCodes.InValidClientName,
                        new Dictionary<string, object?>
                        {
                            // P16-054/P16-080 — error-context echoes the
                            // operator-facing wire name `emailId`.
                            ["field"] = "emailId",
                            ["reason"] = "EmailId is required per spec §4 / REQUIREMENTS §Create.",
                        });
                }

                // TaxType must match the active module per agent 06 §6. The
                // session manager's ApplySimCreateClient does not enforce this
                // (it just appends to the active module store) — so the gate
                // lives here in the tool layer. The live SDK enforces server-
                // side; we just surface the error earlier with the right name.
                //
                // P15-059 — after the accept-gate, canonicalize the operator's
                // (possibly colloquial) alias (e.g. "1040", "1120S",
                // "S-Corporation") to the canonical spec-mandated ReturnType
                // ("Individual", "SCorporate", ...) BEFORE building
                // CreateClientArgs. The live SDK's IClient(TaxType, ...)
                // constructor accepts only the 9 spec strings; passing the
                // form-number variant verbatim would yield TaxModuleMismatch
                // (14) on a happy-path operator request, violating CLAUDE.md's
                // two-mode discipline ("sim and live observationally identical").
                // Canonicalizing here means downstream (manager → sim → bridge)
                // only ever sees the spec alphabet.
                string canonicalTaxType = taxType;
                if (_session.Context != null)
                {
                    var moduleChar = _session.Context.Module;
                    var accepted = Modules.TaxTypeAliasesFor(moduleChar);
                    if (accepted != null && !Modules.AcceptsTaxType(moduleChar, taxType))
                    {
                        throw new LacerteSDKError(
                            StatusCodes.TaxModuleMismatch,
                            new Dictionary<string, object?>
                            {
                                ["taxType"] = taxType,
                                ["module"] = moduleChar.ToString(),
                                ["acceptedTaxTypes"] = accepted,
                                ["reason"] = $"TaxType '{taxType}' is not valid for module '{moduleChar}'.",
                            });
                    }
                    // Accept-gate passed → canonicalization MUST return non-null
                    // (same alias set, same case-insensitive match). The null-
                    // coalesce keeps the local variable nullable-flow happy and
                    // degrades gracefully to the operator-supplied value if a
                    // future divergence between AcceptsTaxType and
                    // CanonicalizeTaxType ever sneaks in.
                    canonicalTaxType = Modules.CanonicalizeTaxType(moduleChar, taxType) ?? taxType;
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
                    ClientId: clientId,
                    TaxType: canonicalTaxType,
                    FirstName: firstName,
                    LastName: lastName,
                    EmailId: emailId);

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
                            ["clientId"] = clientId,
                            ["taxType"] = canonicalTaxType,
                        });
                }

                var info = StatusCodes.GetStatusInfo(result.Status);
                // P16-054/P16-080 — wire envelope key is `clientId` (was
                // `clientID`).
                return (object)new
                {
                    statusCode = result.Status,
                    statusName = info.Name,
                    clientNumber = result.ClientNumber,
                    clientId,
                    dryRun,
                };
            });
        }
    }
}
