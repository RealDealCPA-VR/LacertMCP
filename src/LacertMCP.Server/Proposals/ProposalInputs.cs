using System.Collections.Generic;
using System.ComponentModel;

namespace LacertMCP.Server.Proposals
{
    // Agent-facing input shapes for the proposal MCP tools (the [Description]s
    // become the tool's JSON schema).

    public sealed class SourceCitationInput
    {
        [Description("Source document name, e.g. \"2024 W-2 ACME Corp.pdf\". Required.")]
        public string Document { get; set; } = string.Empty;

        [Description("Page number in the document, if any.")]
        public int? Page { get; set; }

        [Description("Box / line / cell the value came from, e.g. \"Box 1\" or \"Line 7\".")]
        public string? Location { get; set; }

        [Description("Short quote of the source text (optional, at most 1000 characters).")]
        public string? Excerpt { get; set; }
    }

    public sealed class ProposalDetailInput
    {
        [Description("Numeric Series (^\\d+$), from the field-dictionary entry.")]
        public string Series { get; set; } = string.Empty;

        [Description("Numeric Code (integer), from the field-dictionary entry.")]
        public string Code { get; set; } = string.Empty;

        [Description("Amount (number) or null for non-numeric rows.")]
        public double? Amount { get; set; }

        [Description("Row description.")]
        public string Description { get; set; } = string.Empty;

        [Description("Prefix (blank or whole number), as the dictionary entry says.")]
        public string Prefix { get; set; } = string.Empty;

        [Description("Suffix (blank or whole number). Pass the dictionary entry's suffix; live writes require it.")]
        public string Suffix { get; set; } = string.Empty;

        [Description("2-letter state code or empty.")]
        public string StateAbbrev { get; set; } = string.Empty;

        [Description("City abbreviation (max 3 characters) or empty.")]
        public string CityAbbr { get; set; } = string.Empty;

        [Description("SDK detail Source flag: blank, 'Source' or 'Nonsource' (not a form tag).")]
        public string Source { get; set; } = string.Empty;

        [Description("Taxpayer / Spouse / Joint (or T / S / J), or empty.")]
        public string Tsj { get; set; } = string.Empty;

        [Description("Leave blank (read-only per the SDK).")]
        public string ImportSrc { get; set; } = string.Empty;
    }

    public sealed class ProposalLineInput
    {
        [Description("\"field\" (a DATA1..DATA5 field) or \"detail\" (a detail-file row). Required.")]
        public string Kind { get; set; } = string.Empty;

        [Description("Field lines: DBF table 1..5.")]
        public int? Table { get; set; }

        [Description("Field lines: field name (e.g. C1_15), from the field-dictionary entry.")]
        public string? Field { get; set; }

        [Description("Field lines on table 2 / 4: the subclient (partner / shareholder / beneficiary) id.")]
        public int? SubClientId { get; set; }

        [Description("Field lines on table 3 / 4: 2-letter state code.")]
        public string? StateAbbrev { get; set; }

        [Description("Field lines: the value to write (string, number or boolean).")]
        public object? Value { get; set; }

        [Description("Detail lines: the row to append.")]
        public ProposalDetailInput? Detail { get; set; }

        [Description("The lacerte_field_dictionary entry id this line relies on.")]
        public string? DictionaryId { get; set; }

        [Description("Where the value came from (document, page, box/line, excerpt). Required.")]
        public SourceCitationInput? Source { get; set; }

        [Description("Why this value goes here, when there was any judgement (optional).")]
        public string? Reasoning { get; set; }

        [Description("\"high\", \"medium\" or \"low\" — how sure you are of the value and the mapping. Required.")]
        public string Confidence { get; set; } = string.Empty;
    }

    public sealed class ManualItemInput
    {
        [Description("What must be entered by hand. Required.")]
        public string Description { get; set; } = string.Empty;

        [Description("Amount, if any.")]
        public double? Amount { get; set; }

        [Description("Where it came from.")]
        public SourceCitationInput? Source { get; set; }

        [Description("Why it can't be entered automatically (e.g. no verified dictionary mapping). Required.")]
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class QuestionInput
    {
        [Description("The question for the preparer. Required.")]
        public string Text { get; set; } = string.Empty;

        [Description("Line ids the question is about (optional).")]
        public List<string>? LineIds { get; set; }
    }
}
