# Roadmap: AI prepares the return, a human reviews and approves

**Goal.** Claude prepares tax returns in Lacerte end to end: it reads the source documents, enters the data, checks the result and hands the preparer a review packet. The preparer reviews, approves, signs and e-files. The preparer stays responsible for every return.

**Written** 2026-11-18, at v0.2.5. This plan covers the gap between today and that goal. `docs/plans/filing-preparation.md` covers the data-entry foundations already built.

---

## Where things stand (v0.2.5)

| Area | Status |
|---|---|
| Read any return type (clients, owners, states, forms, detail rows, e-file status) | **Done** |
| Create clients | **Done** |
| Write client fields | **Built.** Only fields that are enabled in the access map or verified in the field dictionary |
| Write amounts (detail rows) | **Built, off.** Out-of-process worker, backup, read-back, duplicate protection, dictionary gate, mandatory bulk preview. Not yet verified on the Lacerte PC |
| Undo Claude's amounts (`detail-restore`) | **Built.** The ODBC DELETE is not yet verified on the Lacerte PC |
| Field dictionary (what each Lacerte field/code means) | **Tooling done; content empty.** Mappings are recorded on the PC with `lacerte-mcp map-diff` |
| Guided entry workflow | **Done**: `lacerte_entity_return_entry` for 1120 / 1120-S / 1065 / 1041 and `lacerte_individual_return_entry` for 1040. Both stop until the field dictionary has mappings |
| Pre-filing checks | **Done** for 1040 / 1065 / 1120 / 1120-S / 1041 (structural rules only) |
| See Lacerte's calculated return or diagnostics | **Not possible today.** The SDK exposes inputs, not results |
| Review queue / approval record | **Built** (after v0.2.5): proposals, control-page Proposals tab, `lacerte-mcp proposal`, `-RequireApproval`. Live commit not yet verified on the Lacerte PC |
| Source-document intake and traceability | **Not built** |

---

## What's missing, in priority order

### 1. Live verification (blocks everything that writes)
**Why.** Nothing has written to a real Lacerte return yet, and every later step depends on writes behaving as tested.
**Do (on the Lacerte PC, with a test client):**
- run REGRESSION_CHECKLIST "Round-8 live items", "FP-2 live items" and "FP-2 restore live items";
- then re-run the installer with `-LiveDetailWrites`.

**Exit:** a W-2-type row written, seen in Lacerte's UI, read back as verified, then removed with `detail-restore`.

### 2. Field coverage, the mapping program (the largest piece)
**Why.** Claude can only fill lines that are mapped. Full preparation needs most of each return's input lines: hundreds per return type, many more for 1040.
**Do:**
- **Rank** the lines by how often the firm uses them, per return type. Start with the return type filed most, and within it the lines on most returns: income, the common deductions, officer compensation, the balance sheet, K-1 items, state basics.
- **Map** each line with `map-diff` on a test client: one value in, record, next. Expect a few minutes per line once the routine is set.
- **Look for shortcuts on the PC (worth an hour before mapping by hand):**
  - Does Lacerte's input-screen help show its own field and code numbers?
  - Can an existing, complete return be snapshotted to label many detail rows at once?
- **Ship** mappings in a release (copy them into `src/LacertMCP.Server/Schema/FieldDictionary/` and commit), so every install and future year gets them.
- **Re-check** mappings when Lacerte releases a new tax year; screens change.

**Exit:** for the first return type, at least 90% of the input lines on last year's returns have a verified mapping.

### 3. Seeing the calculated return (decides how much Claude can check itself)
**Why.** The SDK reads what was entered, not what Lacerte computed: form lines, tax due, diagnostics. Without that, Claude can't confirm the return it built is right, and the preparer does all of that checking in Lacerte.
**Investigate on the PC, cheapest first:**
- **DATA6 "Display Info."** It feeds Lacerte's client list, which may hold summary figures (refund or balance due, taxable income). Snapshot it before and after a recalc.
- **Lacerte's exports and reports.** Is there a client summary, a "print to file", or an export of forms and diagnostics that lands somewhere readable?
- **Ask Intuit** whether the SDK has, or plans, any calculated-results or diagnostics access.

**Decide:**
- If an export exists, add a read tool and have Claude compare computed lines with its expectations.
- If not, the review packet (item 4) lists what Claude expects each key line to be, and the preparer confirms those figures in Lacerte.

### 4. A real review and approval step
**Why.** Today approval is per write, in chat, and leaves no record. "AI prepares, I approve" needs:
- one review per return;
- a record of who approved what;
- an easy way to send the return back with corrections.

**Built (2026-11-18), as designed below.** Not yet built: the post-commit review packet, and source documents shown beside each line (item 5).

**Design:**
- **Proposal.** Claude prepares into a proposal, not straight into the return. The proposal is per client and per year, and holds:
  - every intended entry: target, value, the source document and page or box it came from, and Claude's reasoning where there was judgement;
  - the dry-run results;
  - unmapped source lines that must be entered by hand;
  - open questions for the preparer.
- **Review on the control page.** The preparer opens a Proposals tab, reviews line by line (with the source document beside it), edits or rejects lines, and approves.
- **Commit.** Approval commits the proposal in one batch, using the preview-token and journal machinery already built. The approval (who, when, which proposal version) is stored with the journal.
- **Packet afterwards.** After the commit, a review packet shows: what was entered and verified; what still needs hand entry; year-over-year changes (`lacerte_year_over_year`); pre-filing check results; and the key figures Claude expects Lacerte to compute (item 3).
- **Alternative (simpler, weaker).** Keep direct writes and use the packet only for after-the-fact review in Lacerte. That's faster to build, but approval then happens after the data is already in the return.

### 5. Source documents and traceability
**Why.** The preparer must be able to see where every number came from.
**Do:**
- **Intake.** A per-client document folder, or a drop zone on the control page, where source PDFs and images go.
- **Extraction.** Claude extracts values into the proposal with citations (document, page, box or line). Low-confidence reads (handwriting, poor scans) are flagged.
- **Coverage check.** A list of the source documents and whether each was fully used, so nothing silently goes unentered.

Claude can read PDFs and images today, so this is mostly workflow and storage, not new SDK capability.

### 6. Things that may sit outside the tables the SDK exposes
**Why.** Some inputs may not live in DATA1–7 or the detail file:
- depreciation and fixed-asset entry;
- prior-year carryovers and proforma data;
- some elections.

**Do:**
- Use `map-diff` on the PC to see whether entering each one changes anything readable.
- Whatever doesn't show up stays a "preparer enters this" item in the packet.

### 7. A 1040 workflow
**Why.** The guided workflow covered entity returns only, and 1040s are likely most of the volume.
**Built (2026-11-18):** the `lacerte_individual_return_entry` prompt: document inventory confirmed by the operator, one cited line per box per document, taxpayer / spouse ownership, state amounts, judgement calls as questions, a coverage check, then the same approval and commit steps as the entity workflow. It is not yet exercised against real 1040 mappings, because module I has none.
**Still to do:**
- Prioritize 1040 lines in the mapping program.
- Once mappings exist, run the workflow on a test client and adjust it to how Lacerte actually stores W-2s, 1099s and dependents.

### 8. Working alongside staff
**Why.** If a return is open in Lacerte while Claude writes, writes fail or need checking (9022).
**Do:**
- Adopt a simple convention: Claude only works on returns marked "AI prep" and not open.
- Later, check whether the client is open before writing, if the driver or SDK can tell.

### 9. Security and compliance (confirm with your compliance adviser; this is not legal advice)
- **Taxpayer data on disk.** Backups and journals in `%APPDATA%\lacerte-mcp` hold taxpayer data. Add encryption at rest (or keep them on an encrypted volume) and a retention policy, then clean up automatically. Fold this into the firm's written information security plan (FTC Safeguards Rule).
- **Disclosure to an AI provider.** Using a cloud AI model sends return information to a third party. Review how IRC §7216 rules on disclosure and use apply to your setup, whether client consent or provider agreements are needed, and document the decision.
- **Preparer responsibility.** Signing, Circular 230 due diligence and e-filing stay with the preparer, as designed. The approval record (item 4) helps document that review.
- **Access.** The Tailscale device access and the activity log are already in place. Decide how long activity and approval records are kept.

### 10. Measuring accuracy before trusting it
**Why.** Before Claude prepares live returns, you need evidence of how often it's right.
**Do:**
- **Parallel run.** Re-prepare a sample of last year's filed returns from their source documents into test clients, then compare field by field with what was filed.
- **Track** errors by type: mapping gap, extraction error, judgement call.
- **Set a threshold per return type** for moving from "parallel" to "AI prepares, preparer reviews".

---

## Phased plan

| Phase | What | Where | Exit criteria |
|---|---|---|---|
| **A. Prove the plumbing** | Item 1, plus the item 3 investigation | Lacerte PC | Live write, read-back and restore verified; known whether calculated results can be read |
| **B. First return type end to end** | Item 2 for the most-filed type; item 4 (proposal and review); item 5 (documents) | Mostly here; mapping on the PC | One real return prepared as a proposal, reviewed and approved on the control page, committed, verified |
| **C. Measure** | Item 10 on that return type | PC + here | Error rate known; threshold met |
| **D. Widen** | Items 2 and 7 for the remaining types; item 6 findings; item 8 | Both | Each return type passes its own parallel run |
| **E. Harden** | Item 9 (encryption, retention, §7216 review); item 3 compare tool, if possible | Both | Compliance sign-off |

---

## Decisions for you

1. **Approval model.** DECIDED 2026-11-18: proposal-then-approve (see DECISIONS).
2. **First return type** for Phase B.
3. **Who maps fields** on the PC, and how much time per week. This is the pacing item.
4. **Compliance review** of §7216 and data handling before live client data flows through the AI.

## What doesn't change
Signing, e-filing and final responsibility stay with the preparer in Lacerte. The MCP never e-files.
