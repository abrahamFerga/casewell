---
description: 'Rules for Casewell test code — which rung a test belongs on, proving the approval gate over HTTP, and the golden eval format.'
applyTo: 'tests/**/*.cs'
---

# Writing tests

Put a test on the **lowest rung that would have caught the bug**, and run it against the *unfixed*
code first. A regression test never seen red is not a regression test — it is a test of the fix's
own assumptions.

| Project | Rung | Proves |
|---|---|---|
| `Casewell.Legal.Tests` | 2 | domain logic and manifest surface guards |
| `Casewell.IntegrationTests` | 3 | the real host, real Postgres, real migrations, RBAC, approvals |
| `Casewell.IntegrationTests/Evals` | 4 | agent behaviour — routing, gating, protocol |

## Security-shaped tests must go over HTTP

Asserting `RequiresApproval = true` on a descriptor is a **static** check: it proves a flag is set,
not that the gate fires. A product can ship a broken human-in-the-loop gate with fully green CI —
the worst failure available on this platform.

Prove it through `fixture.AdminClient(roles, subject)`, which carries the dev-auth headers and is
the only path that goes through the real authorized, audited pipeline. To assert RBAC, pass a
**narrower** role and assert the 403.

Seed domain fixtures through the hand-edit endpoints (`POST /api/legal/matters`), not the
approval-gated chat tools — otherwise every fixture row needs an approval first.

## Golden evals

One JSON file per case in `Evals/cases/`. Unknown fields fail loudly, so a typo surfaces rather than
silently passing. Every case implicitly asserts `RUN_STARTED` and `RUN_FINISHED` are present and
`RUN_ERROR` is absent.

Add or adjust a case when you change a tool name or `[Description]` (does the intent still route
there), a `RequiresApproval` flag (`expectApproval`, plus the reply must not claim success),
`AgentInstructions`, or an RBAC baseline.

Evals run on the Mock provider, which selects tools by name-token match and fills arguments naively.
They prove the **platform contract**, not real-model reasoning quality — do not write a case that
depends on the model being clever.

## Containers

Rungs 3 and 4 need Docker. Keep the Postgres image at `pgvector/pgvector:pg17`, matching the AppHost:
a product that ships on pg17 and tests on pg16 is testing something it does not ship. The `vector`
type does not exist on stock `postgres` and the migration will fail.
