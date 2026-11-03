# ADR 0001: Slim Firefly III tool results in the gateway

* Status: proposed
* Date: 2026-11-03

## Context

Claude reads the Firefly III administration through `firefly-iii-mcp` behind this gateway, and every
character of a tool result counts against the Claude usage limits. The results are large. Measured in a
live session on 2026-11-03, a `search_transactions` call returning about 25 transactions came to
112,355 characters, and the client cut it off at 100,000. One transaction carries about seventy fields,
most of them null or repeated.

What the upstream does (source of `firefly-iii-mcp` 1.4.0):

* Every Firefly API response becomes one text block, `JSON.stringify(response, null, 2)`: indented JSON.
* Errors come back as ordinary results. `isError` is never set; only the shape of the text differs.
* It serves Streamable HTTP with sessions and an in-memory event store, without `enableJsonResponse`.
  With the MCP TypeScript SDK that means a `tools/call` is answered with an event stream, not JSON.

The upstream is third-party code holding the finances, pinned by tag, and stays unmodified.

Measured on the results of that session (shape and size only, no values), cumulatively, for
`search_transactions`:

| Step | Reduction |
|---|---:|
| Remove the indentation | 35% |
| Also remove nulls and empty arrays | 57% |
| Also remove `pc_*` / `primary_currency_*` copies and `links` | 67% |
| Also trim trailing zeros in amounts | 68% |
| Also keep a fixed set of split fields and drop bookkeeping fields | 82% |
| Also turn the list into columns and rows | 90% |

Rules that lose nothing reach 68%; the 80% target for transaction lists needs a field projection.

## Options

**A. Filter in the gateway.** A YARP response transform rewrites tool results on their way back. It
understands JSON and event-stream responses, and in a stream rewrites only the `data` of an event. It
recognises Firefly results by shape and passes everything else through byte for byte.

**B. Compact facade.** The gateway terminates MCP with the C# MCP SDK: an MCP server towards Claude and
an MCP client towards the upstream. Tool results become typed projections, lists become columns and
rows, and `tools/list` can be trimmed as well.

| | A | B |
|---|---|---|
| Transaction lists | 82% | 90% |
| New dependencies | none | C# MCP SDK |
| Event-stream parsing | in the gateway | in the SDK |
| Sessions, notifications, cancellation | untouched | bridged between two sessions |
| Unknown results byte for byte | yes | hard: the SDK re-serialises every message |
| The Home Assistant instance | unaffected | needs a transparent mode as well |

## Decision

A, with three choices:

1. **Recognise results by Firefly object type, not by tool name.** The JSON:API `type` of the items in
   `data` selects the rules. Every tool that returns transactions (search, by account, by tag, by
   budget, store, update) is covered without a list of tool names, and an error text or another
   upstream never matches.
2. **Rewrite POST responses only.** That is where `tools/call` results come back. GET streams (server
   notifications, resumption by Last-Event-ID) stay with YARP's own copy, which also keeps a long-lived
   stream's activity timeout alive. A result replayed on a resumed GET stream therefore arrives
   unslimmed; that is rare and harmless.
3. **Take the columns-and-rows layout from B as an option, off by default.** The Firefly shape with
   fewer fields meets the target and is what Claude already reads. `Slimming__TransactionLayout=Rows`
   is available when the extra 8 points matter more.

The split projection keeps `source_type` and `destination_type` although the target did not require
them: they tell Claude whether a counterpart is an asset account, which matters when it judges whether
a pair of bookings is a transfer. Group `created_at` and `updated_at` are dropped instead. Account
`iban` and `account_number` stay, because working out how an import matched a counterparty relies on
them; the split-level IBAN copies go.

## Consequences

* Results are smaller for every Firefly instance behind this image, and for any tool that returns
  these object types.
* A field Firefly adds to splits in a later version is dropped until it is added to
  `Slimming__KeepSplitFields`. Other object types lose only the listed fields, so new fields there
  survive.
* The gateway now reads the bodies of POST responses and stops asking the upstream for compression
  while slimming is on. On the fixtures this costs well under the 20 ms per call budget.
* `Slimming__Enabled=false` restores plain proxying without a rebuild.
* The README no longer says the gateway does three things: it does four.

## Verification

* `tests/FireflyMcpGateway.Tests`: size reduction per fixture, field coverage, byte-for-byte passthrough
  of unknown results, error statuses and GET streams, event-stream framing over awkward chunk
  boundaries, the latency budget, and baseline tests for authentication and proxying.
* After deployment: one `search_transactions` call through the connector, reporting only its size
  against the 112,355-character baseline.
