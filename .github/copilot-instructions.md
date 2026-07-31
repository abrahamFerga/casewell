# Casewell — Copilot instructions

**[`AGENTS.md`](../AGENTS.md) is the full contract. Read it.** This file deliberately does *not*
duplicate it — it carries only the minimum a github.com Chat session needs standalone, because Chat
in the browser is the one surface that does not read `AGENTS.md`. Do not "sync" the two; a
contradiction between them resolves nondeterministically.

## What this repo is

Casewell is a thin product host on the **Cortex platform**: auth, multi-tenancy,
RBAC-before-the-model, approvals, audit, jobs, chat, documents and RAG all come from the `Cortex.*`
packages in `.packages/`. This repo owns the **`legal` module** and nothing else. Check whether the
platform already provides a seam before building one.

## Verify

```bash
dotnet build Casewell.slnx -c Release
dotnet test  Casewell.slnx -c Release
```

Integration and eval tests need Docker. Without it, skip them explicitly and say so:

```bash
dotnet test Casewell.slnx --filter "FullyQualifiedName!~IntegrationTests"
```

Running the product is covered in [`RUNBOOK.md`](../RUNBOOK.md).

## The three rules that catch most mistakes

1. **A tool must be declared twice** — in the manifest (`ToolDescriptor`) *and* in
   `IModuleToolSource`. Missing either means it is silently never callable.
2. **`RequiresApproval` is the union of both declarations.** Either alone gates the tool, so a write
   can look gated and not be. Assert the gate over HTTP, never on the flag.
3. **Every entity needs `HasQueryFilter`** — that is tenant isolation, and nothing fails loudly when
   it is missing.

## Pull requests

On `feat/*`, `fix/*` and `chore/*` branches a required check rejects any PR whose body lacks
`Closes #<n>`, a `## Runtime evidence` section, and a `## Regression test` section stating the test
was seen red before the fix and green after.

Removing or editing a line touching `HasQueryFilter`, `RequiresApproval`, a `Permissions.` string,
`.github/`, `CODEOWNERS` or `nuget.config` fails the spine gate and requires a human.
