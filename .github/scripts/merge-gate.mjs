#!/usr/bin/env node
// The merger. Deterministic (verification ladder L1/L2) — no dependencies beyond `gh`, node >= 18.
//
//   node .github/scripts/merge-gate.mjs                 evaluate every open PR, print verdicts
//   node .github/scripts/merge-gate.mjs --pr 131         evaluate one
//   node .github/scripts/merge-gate.mjs --merge          merge what passes, up to the cap
//   node .github/scripts/merge-gate.mjs --fixture f.json evaluate fixture data (used to test itself)
//   node .github/scripts/merge-gate.mjs --runs-fixture r.json   feed main_is_green a run list
//
// This file is the ONE implementation of the merge gates. `/plenipo:ship` runs it rather than
// re-deriving the list in prose, and `agent-merge.yml` runs it on a schedule so merging keeps
// working when the machine that wrote the code is off. An agent can be argued out of a judgement;
// an exit code cannot.
//
// It deliberately does NOT re-check the PR body or the diff. Those are `pr-gates.mjs`, running as a
// REQUIRED status check — so `checks_green` already subsumes them. If `pr-gates` is not required on
// the default branch, this gate is weaker than it looks: `checks_exist` is the only thing standing
// between you and a vacuous green.

import { readFileSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const argv = process.argv.slice(2);
const flag = (name) => argv.includes(name);
const value = (name) => {
  const i = argv.indexOf(name);
  return i === -1 ? undefined : argv[i + 1];
};

const DO_MERGE = flag('--merge');
const ONE_PR = value('--pr');
const FIXTURE = value('--fixture');
const RUNS_FIXTURE = value('--runs-fixture');

// Fixture data describes pull requests that do not exist. Nothing driven by it may reach the
// network — so `--fixture` degrades `--merge` to a simulation rather than being ignored by it.
const SIMULATE = Boolean(FIXTURE);

const gh = (args) => {
  const r = spawnSync('gh', args, { encoding: 'utf8', shell: process.platform === 'win32' });
  if (r.status !== 0) throw new Error(`gh ${args.join(' ')} failed:\n${r.stderr || r.stdout}`);
  return r.stdout;
};

// Same call, but a failure is reported instead of thrown. Used only AFTER a merge has already
// landed: at that point throwing would fail the run and skip every remaining pull request over
// something that is bookkeeping, not safety.
const ghSoft = (args) => {
  const r = spawnSync('gh', args, { encoding: 'utf8', shell: process.platform === 'win32' });
  return { ok: r.status === 0, out: (r.stderr || r.stdout || '').trim().split('\n')[0] ?? '' };
};

// ── Policy, read from the repo — never inferred ───────────────────────────────
// An agent that decides it has earned autonomy is the self-approving loop wearing a different hat.
// Absent config means level 0: review and label, merge nothing.
const cfg = existsSync('workflow.json') ? JSON.parse(readFileSync('workflow.json', 'utf8')) : {};
const autonomy = cfg.autonomy ?? {};
const LEVEL = Number.isInteger(autonomy.level) ? autonomy.level : 0;
const MAX_MERGES = autonomy.maxMergesPerTick ?? 2;

const LOOP_BRANCH = /^(feat|fix|chore)\//;
const HOLD_LABELS = ['human-hold', 'needs-human', 'agent:blocked'];
// Docs, tests and the runbook are the only class a level-1 product may land on its own.
const LOW_RISK = [/\.md$/i, /^tests\//, /\.http$/i, /^\.http$/i];

// ── Platform repos are gated differently, not more leniently ─────────────────
// A product merge risks one product; a platform merge risks every product built on it, and that
// asymmetry GROWS with each consumer rather than shrinking with a good track record. So the platform
// has no autonomy level to earn — it has a stronger verifier: consumer-conformance.yml packs the
// platform as a release candidate and rebuilds every registered consumer against it.
const IS_PLATFORM = String(cfg.stage ?? cfg.kind ?? 'product').toLowerCase() === 'platform';
const CONFORMANCE_CHECK = /consumer.?conformance|conformance verdict/i;
const SURFACE_RE = /^\s*(?:public[- ])?surface:\s*(additive|breaking|none)\b/im;

const PR_FIELDS = [
  'number', 'title', 'body', 'isDraft', 'headRefName', 'baseRefName', 'labels',
  'mergeable', 'mergeStateStatus', 'reviewDecision', 'statusCheckRollup', 'files', 'author',
  // Read rather than parsed out of the body: this is the link GitHub itself acts on, so it also
  // covers an issue attached through the Development sidebar with no keyword in the text.
  'closingIssuesReferences',
].join(',');

// ── Load the pull requests ───────────────────────────────────────────────────
let prs;
if (FIXTURE) {
  prs = JSON.parse(readFileSync(FIXTURE, 'utf8'));
} else if (ONE_PR) {
  prs = [JSON.parse(gh(['pr', 'view', ONE_PR, '--json', PR_FIELDS]))];
} else {
  prs = JSON.parse(gh(['pr', 'list', '--state', 'open', '--limit', '50', '--json', PR_FIELDS]));
}

// ── The default branch must be green before anything lands on it ─────────────
// Merging onto a red base multiplies one failure into N, and the next agent cannot tell which
// change broke what.
//
// Only a run triggered BY the branch's code can speak to whether that code is healthy — an
// issue-triggered triage agent or a nightly maintenance job cannot. Reading a single run across
// every workflow let whichever job finished most recently decide the entire queue, in BOTH
// directions: a cancelled triage run blocked every merge, and a successful one would have waved a
// merge onto genuinely red CI. So this reads `push` events only, and takes the latest verdict per
// WORKFLOW rather than one run overall.
//
// `cancelled` and `skipped` are not evidence of breakage — concurrency groups cancel superseded
// runs constantly — so only a real failure blocks. A gate that blocks on the ABSENCE of evidence
// stops the queue permanently the first time a workflow is skipped.
const BROKEN = ['failure', 'timed_out', 'startup_failure'];

// `gh run list` returns newest first, so the first entry per workflow name is that workflow's
// current verdict. Kept out of the gh call so `--runs-fixture` can prove it red before green.
function brokenWorkflows(runs) {
  const latest = new Map();
  for (const r of runs) if (!latest.has(r.name)) latest.set(r.name, r);
  return [...latest.values()].filter((r) => BROKEN.includes(String(r.conclusion ?? '').toLowerCase()));
}

let mainGreen = true;
let mainWhy = '';

// A function rather than a one-shot block: a merge earlier in this same run puts a new commit on
// the base, so this has to be answerable again rather than answered once. See the re-check in the
// merge loop below.
function refreshMainGreen() {
  mainGreen = true;
  mainWhy = '';
  if (FIXTURE && !RUNS_FIXTURE) return;
  const base = prs[0]?.baseRefName ?? JSON.parse(gh(['repo', 'view', '--json', 'defaultBranchRef']))
    .defaultBranchRef.name;
  const runs = RUNS_FIXTURE
    ? JSON.parse(readFileSync(RUNS_FIXTURE, 'utf8'))
    : JSON.parse(gh(['run', 'list', '--branch', base, '--event', 'push', '--status', 'completed',
      '--limit', '30', '--json', 'conclusion,name']));
  const broken = brokenWorkflows(runs);
  if (broken.length) {
    mainGreen = false;
    mainWhy = `on ${base}: ${broken.map((r) => `${r.name} concluded "${r.conclusion}"`).join(', ')}`;
  }
}

refreshMainGreen();

// ── Gates ────────────────────────────────────────────────────────────────────
function evaluate(pr) {
  const fail = [];
  const labels = (pr.labels ?? []).map((l) => (typeof l === 'string' ? l : l.name).toLowerCase());
  // GitHub's rollup keeps EVERY check run for the head commit, including superseded ones — a check
  // that failed and was then re-run green appears TWICE. Filtering the raw list makes a stale
  // FAILURE permanent: a pull request that ever went red could never merge again however green it
  // became, and the queue stops with a reason that reads like a real failure.
  //
  // Observed, not theorised: after a PR body edit re-triggered `Agent gates`, the rollup held
  //   PR gates | FAILURE | 01:29:48
  //   PR gates | SUCCESS | 01:31:56
  // and `gh pr checks` reported pass while this gate reported "PR gates not passing".
  //
  // EVERY rule below biases toward blocking, because the two failure directions are not
  // symmetrical: refusing a mergeable PR wastes a tick, while merging on a superseded green is
  // unrecoverable. An earlier version of this collapsed to "latest by startedAt", which merged a PR
  // whose re-run was still QUEUED — the queued entry has no timestamp, lost the comparison, and was
  // dropped. That sequence is routine here by design: `agent-gates.yml` re-triggers on `labeled`,
  // ship adds `agent:approved`, and the merge cron fires minutes later.
  //
  // NOTE: this is deliberately NOT the same rule as `brokenWorkflows` below, despite operating on
  // the same idea. That one keys on the WORKFLOW name, takes the first entry trusting `gh run list`
  // to be newest-first, and excludes `cancelled`. This keys on workflow+job (two workflows may both
  // define `build`), cannot trust rollup ordering (observed: the earliest-started entry appearing
  // last), and treats `cancelled` as broken.
  const groups = new Map();
  for (const c of pr.statusCheckRollup ?? []) {
    // Job name alone collides across workflows; qualify it.
    const key = `${c.workflowName ?? ''}/${c.name || c.context || ''}`;
    (groups.get(key) ?? groups.set(key, []).get(key)).push(c);
  }

  const terminal = (c) => (c.conclusion || c.state || c.status || '').toUpperCase();
  const isPending = (c) => ['PENDING', 'IN_PROGRESS', 'QUEUED', 'WAITING', ''].includes(terminal(c));
  // StatusContext entries carry no startedAt at all; legacy commit statuses only have createdAt.
  const when = (c) => c.completedAt ?? c.startedAt ?? c.createdAt ?? '';

  const checks = [...groups.values()].map((runs) => {
    // A re-run in flight means the verdict is not settled, whatever an older run concluded. Report
    // the pending one so `checks_green` says "still running" rather than merging on stale green.
    const inFlight = runs.find(isPending);
    if (inFlight) return inFlight;

    // Latest terminal run wins; on a tie — same-second timestamps are common, one event triggering
    // several runs — prefer the non-SUCCESS, so an ambiguous pair never resolves to "mergeable".
    return runs.reduce((best, c) => {
      if (when(c) > when(best)) return c;
      if (when(c) < when(best)) return best;
      return terminal(best) === 'SUCCESS' ? c : best;
    });
  });

  const files = (pr.files ?? []).map((f) => f.path ?? f.filename ?? '');

  const state = (c) => (c.conclusion || c.state || c.status || '').toUpperCase();
  const pending = checks.filter((c) => ['PENDING', 'IN_PROGRESS', 'QUEUED', 'WAITING', ''].includes(state(c)));
  const broken = checks.filter((c) => ['FAILURE', 'ERROR', 'CANCELLED', 'TIMED_OUT', 'ACTION_REQUIRED'].includes(state(c)));

  const isLowRisk = files.length > 0 && files.every((f) => LOW_RISK.some((re) => re.test(f)));
  const changeClass = isLowRisk ? 'low-risk' : 'feature';

  if (!LOOP_BRANCH.test(pr.headRefName)) fail.push(`is_loop_pr: "${pr.headRefName}" is not a loop branch — not ours to merge`);
  if (!/plenipo-agent/.test(pr.body ?? '')) fail.push('is_loop_pr: the body carries no plenipo-agent envelope');
  if (pr.isDraft) fail.push('not_draft: the PR is a draft');
  if (checks.length === 0) fail.push('checks_exist: no status checks ran — green would mean nothing');
  if (pending.length) fail.push(`checks_green: ${pending.length} check(s) still running`);
  if (broken.length) fail.push(`checks_green: ${broken.map((c) => c.name || c.context).join(', ')} not passing`);
  if (pr.mergeable && pr.mergeable !== 'MERGEABLE') fail.push(`mergeable: mergeable=${pr.mergeable}`);
  if (['DIRTY', 'BLOCKED', 'BEHIND'].includes(pr.mergeStateStatus)) fail.push(`mergeable: mergeStateStatus=${pr.mergeStateStatus}`);
  if (pr.reviewDecision === 'CHANGES_REQUESTED') fail.push('no_blocking_review: a review requested changes');
  if (!labels.includes('agent:approved')) fail.push('agent_approved: no `agent:approved` label — nothing has reviewed this');
  if (labels.includes('agent:changes-requested')) fail.push('agent_approved: `agent:changes-requested` is still set');
  for (const h of HOLD_LABELS) if (labels.includes(h)) fail.push(`no_human_hold: \`${h}\` is set`);
  if (!mainGreen) fail.push(`main_is_green: ${mainWhy}`);

  if (LEVEL === 0) fail.push('level_permits: autonomy level 0 merges nothing — a human decides');
  else if (LEVEL === 1 && changeClass !== 'low-risk') {
    fail.push('level_permits: level 1 may merge docs, tests and the runbook only');
  }

  // ── Platform-only gates ────────────────────────────────────────────────────
  // `checks_green` CANNOT stand in for consumers_green, and this is the whole reason it is a named
  // gate rather than a comment: consumer-conformance.yml carries a `paths:` filter, so a pull
  // request that misses `src/**` never triggers it, the rollup never contains it, and green means
  // "it did not run". That is the `checks_exist` failure mode one level up — a check nobody ran
  // reads exactly like a check that passed.
  if (IS_PLATFORM) {
    const conformance = checks.filter((c) => CONFORMANCE_CHECK.test(c.name || c.context || ''));
    const notGreen = conformance.filter((c) => state(c) !== 'SUCCESS');

    if (conformance.length === 0) {
      fail.push(
        'consumers_green: no consumer-conformance check ran on this PR — a skipped conformance ' +
          'run is a red gate, not a missing one'
      );
    } else if (notGreen.length) {
      fail.push(
        `consumers_green: ${notGreen.map((c) => `${c.name || c.context} (${state(c) || 'no conclusion'})`).join(', ')} ` +
          '— a registered consumer does not build or does not pass against this change'
      );
    }

    const surface = SURFACE_RE.exec(pr.body ?? '');
    if (!surface) {
      fail.push(
        'surface_declared: the body has no "Surface: additive|breaking|none" line — an ' +
          'unclassified break gets announced without migration steps, which starts N agents down ' +
          'an unverified path'
      );
    } else if (surface[1].toLowerCase() === 'breaking' && !labels.includes('human-approved')) {
      fail.push(
        'surface_declared: "Surface: breaking" needs the `human-approved` label — a human writes ' +
          'the migration before every consumer is told to follow it'
      );
    }
  }

  return { pr, fail, changeClass };
}

const results = prs.map(evaluate).sort((a, b) => a.pr.number - b.pr.number);

// ── Close what the merge was supposed to close ───────────────────────────────
// GitHub auto-closes a linked issue AS THE MERGING ACTOR. `agent-merge.yml` merges with
// `GITHUB_TOKEN`, so without `issues: write` that close is silently dropped: the pull request
// closes, the issue stays open, and the board never drains.
//
// Observed on networthy over eight days, matched pairs differing only in who merged — every merge
// by a user token closed its issue on the merge timestamp; every `github-actions[bot]` merge left
// it open (#180 → #150 never closed at all; #177 → #172 and #171 → #149 were closed by hand hours
// later). An unattended loop then re-reads a board still advertising work that is already merged.
//
// The permission is granted now, but relying on the implicit behaviour is exactly what failed
// quietly for a week — so close them here, where the run log says whether it happened.
const linkedIssues = (pr) =>
  (pr.closingIssuesReferences ?? []).map((i) => i?.number).filter((n) => Number.isInteger(n));

function closeLinkedIssues(pr) {
  for (const n of linkedIssues(pr)) {
    const { ok, out } = ghSoft(['issue', 'close', String(n), '--reason', 'completed',
      '--comment', `Closed by #${pr.number}.`]);
    // An issue already closed — by the implicit behaviour, or by a human ahead of the tick — is
    // the goal state, not a failure. Report either way; never let bookkeeping stop the loop.
    console.log(ok ? `         closed #${n}` : `         WARN could not close #${n}: ${out}`);
  }
}

// ── Report, then act ─────────────────────────────────────────────────────────
console.log(`autonomy level ${LEVEL} · ${results.length} open PR(s) · cap ${MAX_MERGES}/run\n`);

let merged = 0;
for (const { pr, fail, changeClass } of results) {
  // Printed for every pull request, on the dry run too, so "this will close nothing" is visible
  // BEFORE the merge rather than inferred from a board that stopped draining. Deliberately not a
  // gate: a `chore/` PR with no issue behind it is legitimate, and a gate that blocks on the
  // absence of a link would stall the queue on exactly the PRs nobody filed an issue for.
  const closes = linkedIssues(pr);
  const closesNote = closes.length
    ? `closes ${closes.map((n) => `#${n}`).join(', ')}`
    : 'closes nothing — no issue is linked to this pull request';

  if (fail.length === 0) {
    if (!DO_MERGE) {
      console.log(`  READY  #${pr.number} ${pr.title} [${changeClass}]`);
      console.log(`         ${closesNote}`);
    } else if (merged >= MAX_MERGES) {
      console.log(`  HELD   #${pr.number} — under_cap: ${MAX_MERGES} already merged this run`);
    } else {
      // ── Re-read the world before the SECOND merge of a run ──────────────────
      // Gates were evaluated once, up front, for every pull request. `mergeable`, `checks_green`
      // and `main_is_green` are assertions about the world RIGHT NOW — that is exactly why they
      // live here rather than in `pr-gates.mjs` — so the moment one merge lands, all three are
      // stale for everything still queued: the base moved, and this PR's checks ran against a
      // commit that is no longer the tip.
      //
      // `/plenipo:ship` documents this script as re-evaluating "every gate before touching
      // anything". That was true PER RUN and false WITHIN one, so with `maxMergesPerTick >= 2`
      // the second merge landed on a verdict nothing re-checked. In practice the re-check usually
      // reports `mergeStateStatus=BEHIND` and holds the PR for the next tick, which is the
      // correct answer: its checks have not run against the base it would land on.
      if (merged > 0) {
        console.log(`  RECHECK #${pr.number} — an earlier merge moved the base; re-reading it`);
        if (!SIMULATE) {
          refreshMainGreen();
          const fresh = evaluate(JSON.parse(gh(['pr', 'view', String(pr.number), '--json', PR_FIELDS])));
          if (fresh.fail.length) {
            console.log(`  BLOCK  #${pr.number} ${pr.title} [${changeClass}] — stale verdict, re-checked`);
            for (const f of fresh.fail) console.log(`         - ${f}`);
            continue;
          }
        }
      }

      // Fixture runs never touch the network — otherwise `--fixture x.json --merge` would try to
      // squash-merge pull requests numbered 901-907 in whatever repo it happened to be run from.
      if (SIMULATE) {
        merged++;
        console.log(`  WOULD MERGE #${pr.number} ${pr.title} [${changeClass}]`);
        console.log(`         ${closesNote}`);
        continue;
      }

      gh(['pr', 'merge', String(pr.number), '--squash', '--delete-branch']);
      merged++;
      console.log(`  MERGED #${pr.number} ${pr.title} [${changeClass}]`);
      console.log(`         ${closesNote}`);
      closeLinkedIssues(pr);
    }
  } else {
    console.log(`  BLOCK  #${pr.number} ${pr.title} [${changeClass}]`);
    console.log(`         ${closesNote}`);
    for (const f of fail) console.log(`         - ${f}`);
  }
}

const blocked = results.filter((r) => r.fail.length).length;
console.log(`\n${results.length - blocked} ready · ${blocked} blocked · ${merged} merged\n`);
// Blocked PRs are the normal state of a healthy queue, not an error — a non-zero exit here would
// turn "waiting for CI" into a failed scheduled run every fifteen minutes.
process.exit(0);
