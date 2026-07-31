---
description: 'Authoring rules for the legal module and host source — tool declaration, the approval gate, permissions, and tenant isolation.'
applyTo: 'src/**/*.cs'
---

# Writing module and host code

Casewell owns the `legal` module; everything security-shaped comes from the Cortex platform. Extend
the platform through its seams — do not reimplement a weaker copy of something it already provides.

## Declaring a tool

A tool must appear in **both** places or it is silently never callable, with no error at startup and
no error at call time:

1. the module manifest, as a `ToolDescriptor`
2. `IModuleToolSource`, as a `ModuleTool`

`GET /api/admin/security/catalog` lists every tool with its permission — if a new tool is missing
there, the two declarations disagree.

Use `Permissions.ForTool(Id, "name")` for the permission string in both places. Hand-written strings
that differ by a character will 403 even a `system_admin`.

## The approval gate

`RequiresApproval` exists on `ToolDescriptor` **and** on `ModuleTool`, and the gate is the **union**:
either one alone gates the tool. The consequence that bites is the inverse — a tool can look gated
on the descriptor while the flag that matters is unset elsewhere. Set both.

Any tool that writes, moves money, or sends something outward is approval-gated. The reply must
disclaim the write; it must never claim an action happened that is still queued.

## Tenant isolation

Every entity on `LegalDbContext` needs its own `HasQueryFilter`. There is no global default and
nothing fails loudly when one is missing — the query simply returns other tenants' rows. Adding an
entity without a filter is the most damaging single-line mistake available in this repo.

## Endpoints

When you add an endpoint, add its request to `casewell.http` in the same pull request. That file is
the runnable catalog the next agent uses to exercise the product.
