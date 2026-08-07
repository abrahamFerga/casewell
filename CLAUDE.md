@AGENTS.md

<!--
Claude Code does not read AGENTS.md on its own — the import above is the bridge. Everything general
lives there; this file carries only what is specific to Claude Code, so the two can never contradict
each other.
-->

# Claude Code specifics

## Skills

- **`/run-casewell`** — the packaged version of [`RUNBOOK.md`](RUNBOOK.md): launch modes, dev-auth
  headers, the AG-UI event contract, the approval round trip, the test ladder. Invoke it instead of
  re-deriving how to start or exercise the product.

The `plenipo-agents` verbs are enabled in [`.claude/settings.json`](.claude/settings.json):
`/deliver:work-next-issue` takes one Ready issue to a PR, `/plenipo:ship` reviews and merges within
`autonomy.level`, `/deliver:verify-runtime` proves a change at runtime.

## Running the app

Use [`.claude/launch.json`](.claude/launch.json) via the preview tools rather than starting a server
in a Bash call — a dev server launched from Bash outlives the turn and then holds the port and the
build output locks.

Prefer **`aspire run`** over `dotnet run` when you intend to read logs or traces: an AppHost started
with `dotnet run` is invisible to the Aspire MCP, which is the agent-readable observability path.

## Verification

Claim only what you ran. If Docker was unavailable and the integration rungs were skipped, say which
rungs you skipped rather than reporting a green ladder — an unqualified "tests pass" that silently
excluded the approval-gate test is the failure mode this product is most exposed to.
