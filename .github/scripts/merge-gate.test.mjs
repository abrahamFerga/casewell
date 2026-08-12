#!/usr/bin/env node
// Self-test for merge-gate.mjs's check-rollup handling. No network, no `gh`, no merging.
//
//   node .github/scripts/merge-gate.test.mjs
//
// `merge-gate.mjs` advertises `--fixture` as "used to test itself". An earlier attempt to fix a
// stale-check bug shipped a REGRESSION — it merged a pull request whose re-run was still queued —
// which a fixture catches in seconds and a live-queue A/B did not.
//
// Asserts on the GATE REASON rather than READY/BLOCK on purpose. A verdict depends on
// `autonomy.level`, so an assertion phrased as "must be READY" would break the day a human lowers
// the level — turning a real check into noise someone silences.

import { spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { delimiter, dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const gate = join(here, 'merge-gate.mjs');
const fixtureSource = join(here, 'fixtures', 'check-rollup.json');
const ENVELOPE = '<!-- plenipo-agent kind=handoff from=trusted-agent ref=fixture-repository#1 status=open -->';
const FIXTURE_REPOSITORY = 'fixture/repository';
const TRUSTED_ACTOR = 'trusted-agent';
const withProvenance = (pr) => ({
  isCrossRepository: false,
  headRepository: { nameWithOwner: FIXTURE_REPOSITORY },
  author: { login: TRUSTED_ACTOR },
  ...pr,
});
const fixtureBundle = (pullRequests, extra = {}) => ({
  repository: { nameWithOwner: FIXTURE_REPOSITORY, defaultBranch: 'main' },
  ...extra,
  pullRequests: pullRequests.map(withProvenance),
});
const workflowPolicy = (level, extra = {}) => ({
  ...extra,
  autonomy: { level, maxMergesPerTick: 20, trustedAuthors: [TRUSTED_ACTOR] },
});

const fixtureScratch = mkdtempSync(join(tmpdir(), 'merge-gate-baseline-'));
writeFileSync(join(fixtureScratch, 'workflow.json'), JSON.stringify(workflowPolicy(0)));
const fixture = join(fixtureScratch, 'check-rollup.json');
writeFileSync(
  fixture,
  JSON.stringify(fixtureBundle(JSON.parse(readFileSync(fixtureSource, 'utf8')).map((pr) => ({
    ...pr,
    body: `${ENVELOPE}\n${pr.body ?? ''}`,
  }))))
);

const run = spawnSync(process.execPath, [gate, '--fixture', fixture], {
  encoding: 'utf8',
  cwd: fixtureScratch,
});
if (run.status !== 0) {
  console.error(`merge-gate exited ${run.status}\n${run.stderr || run.stdout}`);
  process.exit(1);
}

const output = run.stdout;

// Each fixture PR's block is its "#<n>" line plus the indented reasons that follow it.
const reasonsFor = (number) => {
  const lines = output.split('\n');
  const start = lines.findIndex((l) => l.includes(`#${number} `));
  if (start === -1) return null;
  const rest = lines.slice(start + 1);
  const end = rest.findIndex((l) => /^\s{2}(READY|BLOCK|HELD|MERGED)/.test(l));
  return (end === -1 ? rest : rest.slice(0, end)).join('\n');
};

// [pr, must the checks_green gate fire?, what this case is protecting]
const cases = [
  [901, false, 'a stale FAILURE superseded by a fresh SUCCESS must not block — the bug this path exists to fix'],
  [902, true, 'a QUEUED re-run must block: merging on a superseded green is unrecoverable'],
  [903, true, 'identical timestamps must not resolve by array position — prefer the worse conclusion'],
  [904, true, 'two workflows sharing a job name must not collapse into one verdict'],
  [905, true, 'legacy StatusContext entries have no startedAt; createdAt must still order them'],
];

let failed = 0;
for (const [number, mustFail, why] of cases) {
  const reasons = reasonsFor(number);
  if (reasons === null) {
    console.log(`  FAIL #${number} — not present in the gate's output at all`);
    failed++;
    continue;
    }

  const fired = /checks_green/.test(reasons);
  if (fired === mustFail) {
    console.log(`  ok   #${number} — ${why}`);
  } else {
    console.log(`  FAIL #${number} — checks_green ${fired ? 'fired' : 'did NOT fire'}, expected the opposite.\n       ${why}\n       reasons:\n${reasons}`);
    failed++;
  }
}

// ── The linked issue must be named before the merge, not discovered after it ──
// A `GITHUB_TOKEN` merge without `issues: write` closes the pull request and silently leaves the
// issue open, so an unattended board keeps advertising merged work. That failure was invisible
// because nothing in the run log ever mentioned the issue. These assert on the dry run — the step
// `agent-merge.yml` always executes — so the intent is on record even when the merge is skipped.
//
// Asserts on the NOTE rather than on a close actually happening: closing needs `gh` and a live
// repo, which this file deliberately does not have.
const closeCases = [
  [906, /closes abrahamFerga\/Plenipo#150\b/, 'a linked issue must be named in the run log, or a silent no-close is invisible'],
  [907, /closes nothing/, 'a pull request that will close nothing must say so before it merges'],
  [913, /closes other-org\/Other#151\b/, 'a cross-repository linked issue must retain its owner in the run log'],
];

for (const [number, mustMatch, why] of closeCases) {
  const reasons = reasonsFor(number);
  if (reasons === null) {
    console.log(`  FAIL #${number} — not present in the gate's output at all`);
    failed++;
    continue;
  }

  if (mustMatch.test(reasons)) {
    console.log(`  ok   #${number} — ${why}`);
  } else {
    console.log(`  FAIL #${number} — expected ${mustMatch} in the report.\n       ${why}\n       reported:\n${reasons}`);
    failed++;
  }
}

// ── A stale branch is repairable, a conflicted one is not ────────────────────
// `BEHIND` used to sit in the same list as `DIRTY` and `BLOCKED`, and that one line froze the whole
// fleet: the first merge onto main made every other open pull request BEHIND, nothing ever ran
// `gh pr update-branch`, and so the queue absorbed exactly one merge and then stopped. Fourteen of
// twenty-five open PRs across six repos were stuck on this single reason.
//
// These assert on the presence of a `mergeable:` reason rather than on READY/STALE/BLOCK, for the
// same reason as everything above: a verdict depends on `autonomy.level`, a gate reason does not.
const mergeableCases = [
  [908, false, 'BEHIND must not block — it is staleness, and this script can repair it in one call'],
  [909, true, 'DIRTY must still block — a real conflict needs the author, not a branch update'],
  [911, true, 'UNKNOWN must block — only a known-clean state or repairable staleness is safe'],
  [912, false, 'UNSTABLE may pass only because required checks are evaluated independently'],
  [914, true, 'HAS_HOOKS must block — GitHub has an unsatisfied merge requirement'],
];

for (const [number, mustFail, why] of mergeableCases) {
  const reasons = reasonsFor(number);
  if (reasons === null) {
    console.log(`  FAIL #${number} — not present in the gate's output at all`);
    failed++;
    continue;
  }

  const fired = /mergeable:/.test(reasons);
  if (fired === mustFail) {
    console.log(`  ok   #${number} — ${why}`);
  } else {
    console.log(`  FAIL #${number} — the mergeable gate ${fired ? 'fired' : 'did NOT fire'}, expected the opposite.\n       ${why}\n       reasons:\n${reasons}`);
    failed++;
  }
}

// ── Which stale branches actually get updated ────────────────────────────────
// The routing above is level-dependent by construction — a branch is only worth updating when
// freshness is the LAST thing wrong with it, and at level 0 nothing is. So this runs the gate in a
// scratch directory holding a level-3 `workflow.json`, which is the only way to assert the
// STALE-versus-BLOCK split deterministically. The gate reads policy from `workflow.json` in the
// working directory and the fixture path is absolute, so cwd is the whole control surface.
const scratch = mkdtempSync(join(tmpdir(), 'merge-gate-'));
writeFileSync(join(scratch, 'workflow.json'), JSON.stringify(workflowPolicy(3)));

// ── Platform policy — breaking changes carry migration evidence, and conformance follows the
// workflow's path surface ───────────────────────────────────────────────────────────────────────
// Consumer conformance only runs for `src/**` and the root Directory props files. Requiring that
// check for a workflow-only change turns a skipped workflow into a permanent deadlock; skipping it
// for a source change lets a package break through. These three cases prove the two policies stay
// aligned, while the remaining cases prove labels are advisory and explicit holds still block.
const policyScratch = mkdtempSync(join(tmpdir(), 'merge-gate-platform-policy-'));
writeFileSync(
  join(policyScratch, 'workflow.json'),
  JSON.stringify(workflowPolicy(3, { stage: 'platform' }))
);
const policyFixture = join(policyScratch, 'policy-fixture.json');
writeFileSync(
  policyFixture,
  JSON.stringify(fixtureBundle([
    {
      number: 915,
      title: 'label-free workflow-only breaking policy change with migration evidence',
      body: `${ENVELOPE}\nSurface: breaking\n\n## Migration evidence\nConsumers should rename OldApi to NewApi before upgrading; compatibility guidance is published.`,
      isDraft: false,
      headRefName: 'fix/915-policy',
      baseRefName: 'main',
      labels: [],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: '.github/workflows/agent-merge.yml' }],
    },
    {
      number: 916,
      title: 'breaking platform change with insufficient migration evidence',
      body: `${ENVELOPE}\nSurface: breaking\n\n## Migration evidence\nToo short.`,
      isDraft: false,
      headRefName: 'fix/916-policy',
      baseRefName: 'main',
      labels: [],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: '.github/workflows/agent-merge.yml' }],
    },
    {
      number: 917,
      title: 'source change without a conformance result',
      body: `${ENVELOPE}\nSurface: additive`,
      isDraft: false,
      headRefName: 'fix/917-policy',
      baseRefName: 'main',
      labels: [],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'src/Plenipo.Core/Contract.cs' }],
    },
    {
      number: 921,
      title: 'a control change cannot use its own reviewer policy',
      body: `${ENVELOPE}\nSurface: none`,
      isDraft: false,
      headRefName: 'fix/921-self-review',
      baseRefName: 'main',
      labels: [],
      trustedPrGates: false,
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: '.github/workflows/pr-approval-verdict.md' }],
    },
    {
      number: 922,
      title: 'renaming a control file out keeps the old path protected',
      body: `${ENVELOPE}\nSurface: none`,
      isDraft: false,
      headRefName: 'fix/922-rename-control',
      baseRefName: 'main',
      labels: [],
      trustedPrGates: false,
      diff: '--- a/.github/workflows/agent-merge.yml\n+++ b/docs/agent-merge.yml\n',
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'docs/agent-merge.yml' }],
    },
    {
      number: 923,
      title: 'ordinary code is mergeable without an approval label',
      body: `${ENVELOPE}\nSurface: none`,
      isDraft: false,
      headRefName: 'fix/923-unproven-label',
      baseRefName: 'main',
      labels: [],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [
        { name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' },
        { name: 'Conformance verdict', workflowName: 'Consumer conformance', conclusion: 'SUCCESS' },
      ],
      files: [{ path: 'src/Plenipo.Core/Feature.cs' }],
    },
    {
      number: 925,
      title: 'Codex-authored change with no approval label',
      body: '<!-- plenipo-agent kind=handoff from=plenipo-agents ref=plenipo-agents#39 status=open -->\nSurface: none',
      isDraft: false,
      headRefName: 'codex/token-efficient-agent-models',
      baseRefName: 'main',
      labels: [],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'README.md' }],
    },
    {
      number: 926,
      title: 'Codex change carrying only an approval-proof marker',
      body: '<!-- plenipo-agent-verdict:v1 run=300 -->\nSurface: none',
      isDraft: false,
      headRefName: 'codex/verdict-marker-only',
      baseRefName: 'main',
      labels: [],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'README.md' }],
    },
    ...[
      {
        number: 932,
        title: 'forked pull request',
        isCrossRepository: true,
        headRepository: { nameWithOwner: 'untrusted/fork' },
      },
      {
        number: 933,
        title: 'missing opening envelope',
        body: 'The words plenipo-agent appear later, but this is not an opening protocol envelope.\nSurface: none',
      },
      {
        number: 934,
        title: 'untrusted actor',
        author: { login: 'unknown-contributor' },
      },
      {
        number: 935,
        title: 'wrong base branch',
        baseRefName: 'release',
      },
      {
        number: 936,
        title: 'missing repository provenance',
        isCrossRepository: undefined,
        headRepository: undefined,
        author: undefined,
      },
    ].map((pr) => ({
      body: `${ENVELOPE}\nSurface: none`,
      isDraft: false,
      headRefName: `fix/${pr.number}-provenance`,
      baseRefName: 'main',
      labels: [],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'tests/X.cs' }],
      ...pr,
    })),
    ...[
      [927, 'agent changes requested', [{ name: 'agent:changes-requested' }], ''],
      [928, 'human hold', [{ name: 'human-hold' }], ''],
      [929, 'needs human', [{ name: 'needs-human' }], ''],
      [930, 'agent blocked', [{ name: 'agent:blocked' }], ''],
      [931, 'blocking GitHub review', [], 'CHANGES_REQUESTED'],
    ].map(([number, title, labels, reviewDecision]) => ({
      number,
      title,
      body: `${ENVELOPE}\nSurface: none`,
      isDraft: false,
      headRefName: `fix/${number}-explicit-hold`,
      baseRefName: 'main',
      labels,
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision,
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'tests/X.cs' }],
    })),
  ]))
);

const policyRun = spawnSync(process.execPath, [gate, '--fixture', policyFixture], {
  encoding: 'utf8',
  cwd: policyScratch,
});

if (policyRun.status !== 0) {
  console.log(`  FAIL — platform policy fixture exited ${policyRun.status}\n${policyRun.stderr || policyRun.stdout}`);
  failed++;
} else {
  const policyReasons = (number) => {
    const lines = policyRun.stdout.split('\n');
    const start = lines.findIndex((line) => line.includes(`#${number} `));
    if (start === -1) return null;
    const rest = lines.slice(start + 1);
    const end = rest.findIndex((line) => /^\s{2}(READY|BLOCK|HELD|MERGED)/.test(line));
    return (end === -1 ? rest : rest.slice(0, end)).join('\n');
  };

  const ready915 = policyRun.stdout.split('\n').find((line) => line.includes('#915 '));
  const reasons915 = policyReasons(915);
  if (ready915 && /^\s{2}READY\b/.test(ready915) && !/surface_declared|consumers_green/.test(reasons915 ?? '')) {
    console.log('  ok   #915 — a label-free breaking policy change merges with substantive migration evidence');
  } else {
    console.log(`  FAIL #915 — expected READY without surface/conformance failures; got:\n       ${ready915 ?? '(missing)'}\n${reasons915 ?? ''}`);
    failed++;
  }

  const reasons916 = policyReasons(916) ?? '';
  if (/migration_evidence: .*more than 40/i.test(reasons916)) {
    console.log('  ok   #916 — a breaking platform surface without substantive migration evidence remains blocked');
  } else {
    console.log(`  FAIL #916 — the breaking-surface rule did not require substantive migration evidence:\n${reasons916}`);
    failed++;
  }

  const reasons917 = policyReasons(917) ?? '';
  if (/consumers_green/.test(reasons917)) {
    console.log('  ok   #917 — a platform source change still requires consumer conformance');
  } else {
    console.log(`  FAIL #917 — a platform source change lost its conformance requirement:\n${reasons917}`);
    failed++;
  }

  const reasons921 = policyReasons(921) ?? '';
  if (/trusted_pr_gates: .*protected-base PR gate rejected/i.test(reasons921)) {
    console.log('  ok   #921 — a PR-owned required-check wrapper cannot replace the protected-base evaluator');
  } else {
    console.log(`  FAIL #921 — a self-modifying control change was not rejected:\n${reasons921}`);
    failed++;
  }

  const reasons922 = policyReasons(922) ?? '';
  if (/trusted_pr_gates: .*protected-base PR gate rejected/i.test(reasons922)) {
    console.log('  ok   #922 — renaming a control file out of the control tree cannot hide its old path');
  } else {
    console.log(`  FAIL #922 — a control rename evaded the protected-base evaluator:\n${reasons922}`);
    failed++;
  }

  const ready923 = policyRun.stdout.split('\n').find((line) => line.includes('#923 '));
  if (ready923 && /^\s{2}READY\b/.test(ready923)) {
    console.log('  ok   #923 — ordinary code is eligible for unattended merge without any approval label');
  } else {
    console.log(`  FAIL #923 — a label-free ordinary PR was not READY:\n       ${ready923 ?? '(missing)'}`);
    failed++;
  }

  const ready925 = policyRun.stdout.split('\n').find((line) => line.includes('#925 '));
  if (ready925 && /^\s{2}READY\b/.test(ready925)) {
    console.log('  ok   #925 — a Codex-authored PR needs a protocol envelope, not an approval label');
  } else {
    console.log(`  FAIL #925 — Codex-authored PR was excluded from the merge queue:\n       ${ready925 ?? '(missing)'}`);
    failed++;
  }

  const reasons926 = policyReasons(926) ?? '';
  if (/protocol_envelope: .*open/i.test(reasons926)) {
    console.log('  ok   #926 — a Codex branch still needs the same opening protocol envelope');
  } else {
    console.log(`  FAIL #926 — a verdict proof marker entered the merge queue:\n${reasons926}`);
    failed++;
  }

  const explicitBlocks = [
    [927, /no_blocking_review: .*agent:changes-requested/i, '`agent:changes-requested`'],
    [928, /no_human_hold: .*human-hold/i, '`human-hold`'],
    [929, /no_human_hold: .*needs-human/i, '`needs-human`'],
    [930, /no_human_hold: .*agent:blocked/i, '`agent:blocked`'],
    [931, /no_blocking_review: a review requested changes/i, 'a CHANGES_REQUESTED review'],
  ];
  for (const [number, expected, description] of explicitBlocks) {
    const reasons = policyReasons(number) ?? '';
    if (expected.test(reasons)) {
      console.log(`  ok   #${number} — ${description} remains an explicit merge block`);
    } else {
      console.log(`  FAIL #${number} — ${description} did not block:\n${reasons}`);
      failed++;
    }
  }

  const provenanceBlocks = [
    [932, /provenance: .*same repository/i, 'a fork'],
    [933, /protocol_envelope: .*open/i, 'a non-opening marker'],
    [934, /trusted_author: .*unknown-contributor/i, 'an untrusted actor'],
    [935, /base_branch: .*default branch.*main/i, 'a non-default base'],
    [936, /provenance: .*missing/i, 'missing repository/actor provenance'],
  ];
  for (const [number, expected, description] of provenanceBlocks) {
    const reasons = policyReasons(number) ?? '';
    if (expected.test(reasons)) {
      console.log(`  ok   #${number} — ${description} fails closed`);
    } else {
      console.log(`  FAIL #${number} — ${description} passed provenance gates:\n${reasons}`);
      failed++;
    }
  }
}

// ── Required checks, not every informational workflow ──────────────────────
// A Copilot outage in the comment-only intent reviewer is not failed product CI. Branch
// protection names the deterministic CI checks that must actually be green.
// This fixture models one required check plus an advisory `agent` job that failed externally.
const advisoryScratch = mkdtempSync(join(tmpdir(), 'merge-gate-advisory-check-'));
writeFileSync(
  join(advisoryScratch, 'workflow.json'),
  JSON.stringify(workflowPolicy(3, { stage: 'platform' }))
);
const advisoryFixture = join(advisoryScratch, 'required-checks-fixture.json');
writeFileSync(
  advisoryFixture,
  JSON.stringify(fixtureBundle([
      {
        number: 918,
        title: 'a failed advisory agent job must not block required CI',
        body: `${ENVELOPE}\nSurface: none`,
        isDraft: false,
        headRefName: 'fix/918-advisory',
        baseRefName: 'main',
        labels: [],
        mergeable: 'MERGEABLE',
        mergeStateStatus: 'CLEAN',
        reviewDecision: '',
        statusCheckRollup: [
          { name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' },
          { name: 'agent', workflowName: 'Review platform pull request intent', conclusion: 'FAILURE' },
        ],
        files: [{ path: 'tests/X.cs' }],
      },
      {
        number: 919,
        title: 'a missing required check still blocks despite an advisory success',
        body: `${ENVELOPE}\nSurface: none`,
        isDraft: false,
        headRefName: 'fix/919-required',
        baseRefName: 'main',
        labels: [],
        mergeable: 'MERGEABLE',
        mergeStateStatus: 'CLEAN',
        reviewDecision: '',
        statusCheckRollup: [{ name: 'agent', workflowName: 'Review platform pull request intent', conclusion: 'SUCCESS' }],
        files: [{ path: 'tests/X.cs' }],
      },
      {
        number: 924,
        title: 'failed optional Terraform check still blocks an infra change',
        body: `${ENVELOPE}\nSurface: none`,
        isDraft: false,
        headRefName: 'fix/924-infra',
        baseRefName: 'main',
        labels: [],
        mergeable: 'MERGEABLE',
        mergeStateStatus: 'UNSTABLE',
        reviewDecision: '',
        statusCheckRollup: [
          { name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' },
          { name: 'fmt / validate / plan', workflowName: 'Terraform PR Check', conclusion: 'FAILURE' },
        ],
        files: [{ path: 'infra/main.tf' }],
      },
    ], { requiredCheckContexts: ['PR gates'] }))
);

const advisoryRun = spawnSync(process.execPath, [gate, '--fixture', advisoryFixture], {
  encoding: 'utf8',
  cwd: advisoryScratch,
});
if (advisoryRun.status !== 0) {
  console.log(`  FAIL — required-check fixture exited ${advisoryRun.status}\n${advisoryRun.stderr || advisoryRun.stdout}`);
  failed++;
} else {
  const line918 = advisoryRun.stdout.split('\n').find((line) => line.includes('#918 '));
  if (line918 && /^\s{2}READY\b/.test(line918)) {
    console.log('  ok   #918 — an advisory model outage does not turn green required CI red');
  } else {
    console.log(`  FAIL #918 — expected READY with only required CI considered; got:\n       ${line918 ?? '(missing)'}`);
    failed++;
  }

  const reasons919 = advisoryRun.stdout
    .split('\n')
    .slice(advisoryRun.stdout.split('\n').findIndex((line) => line.includes('#919 ')) + 1)
    .join('\n');
  if (/checks_green: required check.*PR gates/i.test(reasons919)) {
    console.log('  ok   #919 — a missing required context remains a hard block');
  } else {
    console.log(`  FAIL #919 — missing required CI was not reported:\n${reasons919}`);
    failed++;
  }

  const reasons924 = advisoryRun.stdout
    .split('\n')
    .slice(advisoryRun.stdout.split('\n').findIndex((line) => line.includes('#924 ')) + 1)
    .join('\n');
  if (/infra_green: fmt \/ validate \/ plan \(FAILURE\) not passing/.test(reasons924)) {
    console.log('  ok   #924 — an infra-scoped Terraform failure remains blocking while unrelated optional checks stay advisory');
  } else {
    console.log(`  FAIL #924 — accepting UNSTABLE hid a Terraform failure:\n${reasons924}`);
    failed++;
  }
}

const levelled = spawnSync(process.execPath, [gate, '--fixture', fixture], {
  encoding: 'utf8',
  cwd: scratch,
});

if (levelled.status !== 0) {
  console.log(`  FAIL — the gate exited ${levelled.status} under a level-3 policy\n${levelled.stderr || levelled.stdout}`);
  failed++;
} else {
  // [pr, the verdict its line must carry, what this case is protecting]
  const routing = [
    [908, 'STALE', 'a PR that passes every gate but freshness must be offered a branch update'],
    [909, 'BLOCK', 'a conflicted PR must never be routed to update-branch'],
    [910, 'STALE', 'a label-free PR that is only behind must be offered a branch update'],
  ];

  for (const [number, verdict, why] of routing) {
    const line = levelled.stdout.split('\n').find((l) => l.includes(`#${number} `));
    if (line === undefined) {
      console.log(`  FAIL #${number} — not present in the level-3 output at all`);
      failed++;
    } else if (new RegExp(`^\\s{2}${verdict}\\b`).test(line)) {
      console.log(`  ok   #${number} — ${why}`);
    } else {
      console.log(`  FAIL #${number} — expected ${verdict}, got:\n       ${line.trim()}\n       ${why}`);
      failed++;
    }
  }
}

// ── `--fixture --merge` must never touch the network ─────────────────────────
// Fixture data describes pull requests numbered 901-910 that exist nowhere. If `--merge` did not
// degrade to a simulation, running this very test file with the merge flag would try to squash
// pull request #901 in whatever repo the runner happened to be sitting in — and on a product repo
// those numbers are real. The failure mode is not a wrong verdict, it is a wrong merge.
const simulated = spawnSync(process.execPath, [gate, '--fixture', fixture, '--merge'], {
  encoding: 'utf8',
  cwd: scratch,
});

if (simulated.status !== 0) {
  console.log(`  FAIL — \`--fixture --merge\` exited ${simulated.status}; it must simulate, not call gh\n${(simulated.stderr || simulated.stdout).split('\n').slice(0, 4).join('\n')}`);
  failed++;
} else if (!/WOULD (MERGE|UPDATE)/.test(simulated.stdout)) {
  console.log(`  FAIL — \`--fixture --merge\` produced no WOULD MERGE/UPDATE line, so nothing proves it simulated`);
  failed++;
} else if (/^\s{2}(MERGED|UPDATE) /m.test(simulated.stdout)) {
  console.log(`  FAIL — \`--fixture --merge\` reported a REAL merge or branch update on fixture data`);
  failed++;
} else if (!/WOULD CLOSE other-org\/Other#151 with --repo other-org\/Other/.test(simulated.stdout)) {
  console.log('  FAIL — `--fixture --merge` did not preserve the linked issue repository in its simulated close command');
  failed++;
} else {
  console.log('  ok   simulate — `--fixture --merge` simulates, never reaches the network, and preserves issue ownership');
}

// A truncated file page must never be classified as low-risk. Otherwise a large PR can put docs
// in the visible page, code in the hidden remainder, and slip through autonomy level 1.
const truncatedScratch = mkdtempSync(join(tmpdir(), 'merge-gate-truncated-files-'));
writeFileSync(join(truncatedScratch, 'workflow.json'), JSON.stringify(workflowPolicy(1)));
const truncatedFixture = join(truncatedScratch, 'fixture.json');
writeFileSync(
  truncatedFixture,
  JSON.stringify(fixtureBundle([{
      number: 920,
      title: 'visible docs page hides additional files',
      body: ENVELOPE,
      isDraft: false,
      headRefName: 'fix/920-truncated',
      baseRefName: 'main',
      labels: [],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      changedFiles: 101,
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'docs/README.md' }],
    }], { requiredCheckContexts: ['PR gates'] }))
);
const truncatedRun = spawnSync(process.execPath, [gate, '--fixture', truncatedFixture], {
  encoding: 'utf8',
  cwd: truncatedScratch,
});
if (truncatedRun.status === 0 && /BLOCK\s+#920\b/.test(truncatedRun.stdout) &&
    /level_permits: level 1 may merge docs, new tests and the runbook only/.test(truncatedRun.stdout)) {
  console.log('  ok   truncation — an incomplete file page cannot masquerade as a low-risk PR');
} else {
  console.log(`  FAIL — an incomplete file page bypassed autonomy level 1:\n${truncatedRun.stdout}${truncatedRun.stderr}`);
  failed++;
}

// ── An unreadable required-check policy is infrastructure failure ───────────
// Ordinary blocked PRs keep the schedule green. Losing the API surface that defines required CI
// must not: that exact green-no-op ran for days while the merger was incapable of merging anything.
const failureScratch = mkdtempSync(join(tmpdir(), 'merge-gate-policy-read-'));
const fakeBin = join(failureScratch, 'bin');
mkdirSync(fakeBin);
writeFileSync(join(failureScratch, 'workflow.json'), JSON.stringify(workflowPolicy(3)));
const mockGh = join(fakeBin, 'gh-mock.mjs');
writeFileSync(
  mockGh,
  `const args = process.argv.slice(2);\n` +
    `if (args[0] === 'pr' && args[1] === 'view') {\n` +
    `  console.log(JSON.stringify({ number: 999, title: 'policy read failure', body: '${ENVELOPE}', isDraft: false, headRefName: 'fix/999-policy-read', baseRefName: 'main', isCrossRepository: false, headRepository: { nameWithOwner: 'example/repo' }, author: { login: '${TRUSTED_ACTOR}' }, labels: [], mergeable: 'MERGEABLE', mergeStateStatus: 'CLEAN', reviewDecision: '', statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }], files: [{ path: 'tests/X.cs' }] }));\n` +
    `  process.exit(0);\n` +
    `}\n` +
    `if (args[0] === 'repo' && args[1] === 'view') { console.log('{"nameWithOwner":"example/repo","defaultBranchRef":{"name":"main"}}'); process.exit(0); }\n` +
    `console.error('required-check query unavailable');\n` +
    `process.exit(1);\n`
);

if (process.platform === 'win32') {
  writeFileSync(join(fakeBin, 'gh.cmd'), `@echo off\r\n"${process.execPath}" "%~dp0\\gh-mock.mjs" %*\r\n`);
} else {
  const shim = join(fakeBin, 'gh');
  writeFileSync(shim, `#!/bin/sh\nexec "${process.execPath}" "$(dirname "$0")/gh-mock.mjs" "$@"\n`);
  chmodSync(shim, 0o755);
}

const policyReadFailure = spawnSync(process.execPath, [gate, '--pr', '999'], {
  encoding: 'utf8',
  cwd: failureScratch,
  env: { ...process.env, GITHUB_ACTIONS: 'true', PATH: `${fakeBin}${delimiter}${process.env.PATH ?? ''}` },
});
const policyReadOutput = `${policyReadFailure.stdout}${policyReadFailure.stderr}`;
if (policyReadFailure.status !== 0 && /checks_configured: could not read required checks/.test(policyReadOutput) &&
    /::error title=Merge gate infrastructure failure::/.test(policyReadOutput)) {
  console.log('  ok   infrastructure — an unreadable required-check policy makes the scheduled merger red');
} else {
  console.log(`  FAIL — required-check discovery failed without failing the merger visibly:\n${policyReadOutput}`);
  failed++;
}

// `gh pr checks` uses exit 8 while checks are pending. Valid JSON on that status is queue state,
// not a broken policy API, and must not prevent unrelated ready PRs from being evaluated.
const pendingScratch = mkdtempSync(join(tmpdir(), 'merge-gate-pending-checks-'));
const pendingBin = join(pendingScratch, 'bin');
mkdirSync(pendingBin);
writeFileSync(join(pendingScratch, 'workflow.json'), JSON.stringify(workflowPolicy(3)));
const pendingGh = join(pendingBin, 'gh-mock.mjs');
writeFileSync(
  pendingGh,
  `const args = process.argv.slice(2);\n` +
    `if (args[0] === 'pr' && args[1] === 'view') {\n` +
    `  console.log(JSON.stringify({ number: 998, title: 'pending required check', body: '${ENVELOPE}', isDraft: false, headRefName: 'fix/998-pending', headRefOid: '${'8'.repeat(40)}', baseRefName: 'main', isCrossRepository: false, headRepository: { nameWithOwner: 'example/repo' }, author: { login: '${TRUSTED_ACTOR}' }, labels: [], mergeable: 'MERGEABLE', mergeStateStatus: 'CLEAN', reviewDecision: '', statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', status: 'IN_PROGRESS' }], files: [{ path: 'tests/X.cs' }] }));\n` +
    `  process.exit(0);\n` +
    `}\n` +
    `if (args[0] === 'pr' && args[1] === 'checks') {\n` +
    `  console.log(JSON.stringify([{ name: 'PR gates' }]));\n` +
    `  process.exit(8);\n` +
    `}\n` +
    `if (args[0] === 'pr' && args[1] === 'diff') { console.log('diff --git a/tests/X.cs b/tests/X.cs'); process.exit(0); }\n` +
    `if (args[0] === 'repo' && args[1] === 'view') { console.log('{"nameWithOwner":"example/repo","defaultBranchRef":{"name":"main"}}'); process.exit(0); }\n` +
    `console.error('unexpected gh call: ' + args.join(' '));\n` +
    `process.exit(2);\n`
);
if (process.platform === 'win32') {
  writeFileSync(join(pendingBin, 'gh.cmd'), `@echo off\r\n"${process.execPath}" "%~dp0\\gh-mock.mjs" %*\r\n`);
} else {
  const shim = join(pendingBin, 'gh');
  writeFileSync(shim, `#!/bin/sh\nexec "${process.execPath}" "$(dirname "$0")/gh-mock.mjs" "$@"\n`);
  chmodSync(shim, 0o755);
}
const pendingCheck = spawnSync(process.execPath, [gate, '--pr', '998'], {
  encoding: 'utf8',
  cwd: pendingScratch,
  env: { ...process.env, GITHUB_ACTIONS: 'true', PATH: `${pendingBin}${delimiter}${process.env.PATH ?? ''}` },
});
const pendingOutput = `${pendingCheck.stdout}${pendingCheck.stderr}`;
if (pendingCheck.status === 0 && /checks_green: 1 check\(s\) still running/.test(pendingOutput) &&
    !/Merge gate infrastructure failure/.test(pendingOutput)) {
  console.log('  ok   pending — exit 8 with valid required-check JSON is ordinary queue state');
} else {
  console.log(`  FAIL — a pending required check stopped the merger infrastructure:\n${pendingOutput}`);
  failed++;
}

// A control-plane change must execute the PR gate fetched from the protected base even when no
// approval label exists. The downloaded script writes a marker so READY alone cannot false-pass.
const baseGateScratch = mkdtempSync(join(tmpdir(), 'merge-gate-protected-base-'));
const baseGateBin = join(baseGateScratch, 'bin');
const baseGateHead = 'a'.repeat(40);
mkdirSync(baseGateBin);
writeFileSync(join(baseGateScratch, 'workflow.json'), JSON.stringify(workflowPolicy(3)));
const baseGateGh = join(baseGateBin, 'gh-mock.mjs');
writeFileSync(
  baseGateGh,
  `const args = process.argv.slice(2);\n` +
    `if (args[0] === 'pr' && args[1] === 'view') {\n` +
    `  console.log(JSON.stringify({ number: 997, title: 'label-free control change', body: '${ENVELOPE}', isDraft: false, headRefName: 'fix/997-control', headRefOid: '${baseGateHead}', baseRefName: 'main', isCrossRepository: false, headRepository: { nameWithOwner: 'example/repo' }, author: { login: '${TRUSTED_ACTOR}' }, labels: [], mergeable: 'MERGEABLE', mergeStateStatus: 'CLEAN', reviewDecision: '', statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }], files: [{ path: '.github/workflows/agent-merge.yml' }] }));\n` +
    `  process.exit(0);\n` +
  `}\n` +
  `if (args[0] === 'pr' && args[1] === 'checks') { console.log('[{"name":"PR gates"}]'); process.exit(0); }\n` +
  `if (args[0] === 'pr' && args[1] === 'diff') { console.log('--- a/.github/workflows/agent-merge.yml\\n+++ b/.github/workflows/agent-merge.yml'); process.exit(0); }\n` +
  `if (args[0] === 'repo' && args[1] === 'view') { console.log('{"nameWithOwner":"example/repo","defaultBranchRef":{"name":"main"}}'); process.exit(0); }\n` +
  `if (args[0] === 'api' && args.some((arg) => arg.includes('contents/.github/scripts/pr-gates.mjs'))) { console.log("import { writeFileSync } from 'node:fs'; if (process.env.PR_HEAD_SHA !== '${baseGateHead}') process.exit(9); writeFileSync('protected-base-invoked', process.env.PR_HEAD_SHA);"); process.exit(0); }\n` +
    `console.error('unexpected gh call: ' + args.join(' '));\n` +
    `process.exit(2);\n`
);
if (process.platform === 'win32') {
  writeFileSync(join(baseGateBin, 'gh.cmd'), `@echo off\r\n"${process.execPath}" "%~dp0\\gh-mock.mjs" %*\r\n`);
} else {
  const shim = join(baseGateBin, 'gh');
  writeFileSync(shim, `#!/bin/sh\nexec "${process.execPath}" "$(dirname "$0")/gh-mock.mjs" "$@"\n`);
  chmodSync(shim, 0o755);
}
const baseGateRun = spawnSync(process.execPath, [gate, '--pr', '997'], {
  encoding: 'utf8',
  cwd: baseGateScratch,
  env: { ...process.env, PATH: `${baseGateBin}${delimiter}${process.env.PATH ?? ''}` },
});
const baseGateOutput = `${baseGateRun.stdout}${baseGateRun.stderr}`;
if (baseGateRun.status === 0 && /READY\s+#997\b/.test(baseGateOutput) &&
    existsSync(join(baseGateScratch, 'protected-base-invoked')) &&
    readFileSync(join(baseGateScratch, 'protected-base-invoked'), 'utf8') === baseGateHead &&
    !/trusted_pr_gates:/.test(baseGateOutput)) {
  console.log('  ok   protected base — a label-free control change executes the trusted base evaluator');
} else {
  console.log(`  FAIL — a label-free control change did not execute the protected-base evaluator:\n${baseGateOutput}`);
  failed++;
}

// Level 1 may add test coverage, but modifying or deleting an existing test can weaken the proof
// surface. Both diff forms must remain feature-class even though every visible path is tests/**.
const testMutationScratch = mkdtempSync(join(tmpdir(), 'merge-gate-test-mutations-'));
writeFileSync(join(testMutationScratch, 'workflow.json'), JSON.stringify(workflowPolicy(1)));
const testMutationFixture = join(testMutationScratch, 'fixture.json');
writeFileSync(
  testMutationFixture,
  JSON.stringify(fixtureBundle([
    {
      number: 937,
      title: 'modify an existing test',
      body: ENVELOPE,
      isDraft: false,
      headRefName: 'fix/937-modify-test',
      baseRefName: 'main',
      labels: [],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'tests/Existing.test.js' }],
      diff: '--- a/tests/Existing.test.js\n+++ b/tests/Existing.test.js\n@@ -1 +1 @@\n-old assertion\n+weaker assertion\n',
    },
    {
      number: 938,
      title: 'delete an existing test',
      body: ENVELOPE,
      isDraft: false,
      headRefName: 'fix/938-delete-test',
      baseRefName: 'main',
      labels: [],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'tests/Existing.test.js' }],
      diff: '--- a/tests/Existing.test.js\n+++ /dev/null\n@@ -1 +0,0 @@\n-old assertion\n',
    },
    {
      number: 939,
      title: 'add a new test',
      body: ENVELOPE,
      isDraft: false,
      headRefName: 'fix/939-add-test',
      baseRefName: 'main',
      labels: [],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'tests/New.test.js' }],
      diff: '--- /dev/null\n+++ b/tests/New.test.js\n@@ -0,0 +1 @@\n+new assertion\n',
    },
  ], { requiredCheckContexts: ['PR gates'] }))
);
const testMutationRun = spawnSync(process.execPath, [gate, '--fixture', testMutationFixture], {
  encoding: 'utf8',
  cwd: testMutationScratch,
});
const testMutationOutput = `${testMutationRun.stdout}${testMutationRun.stderr}`;
if (testMutationRun.status === 0 && /BLOCK\s+#937\b/.test(testMutationOutput) &&
    /BLOCK\s+#938\b/.test(testMutationOutput) && /READY\s+#939\b/.test(testMutationOutput)) {
  console.log('  ok   test mutations — level 1 admits test-only additions but blocks modification and deletion');
} else {
  console.log(`  FAIL — level 1 test mutation policy is wrong:\n${testMutationOutput}`);
  failed++;
}

if (failed) {
  console.log(`\n${failed} rollup case(s) wrong. merge-gate is the last automated thing before main — do not merge this.\n`);
  process.exit(1);
}
console.log(`\nOK — ${cases.length} rollup, ${closeCases.length} linked-issue, ${mergeableCases.length} mergeable, 13 platform-policy, 3 required-context, 3 stale-routing, 1 truncation, 1 simulation, 1 pending-state, 1 protected-base and 1 infrastructure-failure case(s) behave correctly.\n`);
