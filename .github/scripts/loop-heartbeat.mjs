#!/usr/bin/env node
// The deadman switch. Deterministic (verification ladder L1) — no AI, no secrets beyond
// GITHUB_TOKEN, no third-party actions.
//
//   node .github/scripts/loop-heartbeat.mjs              assess and print
//   node .github/scripts/loop-heartbeat.mjs --report     assess, print, and upsert the issue
//   node .github/scripts/loop-heartbeat.mjs --fixture f.json   assess fixture state (tests itself)
//
// Every gate in this repo answers "may this merge?". None of them answers "is anything happening
// at all?" — and every real outage observed here failed on exactly that axis, silently, with a
// green dashboard:
//
//   · 32 workflow runs sat QUEUED across five repos for two hours. No runner picked them up. No
//     workflow failed, so nothing anywhere went red.
//   · The reviewer that applies `agent:approved` died on `HTTP 403` from its model provider. The
//     merger requires that label, so nothing could merge — and the merger's own runs stayed green,
//     because "no pull request is ready" is its normal, healthy output.
//
// A loop that has stopped producing looks exactly like a loop with nothing to do. That ambiguity
// is the failure mode this file exists to remove: it fires on ABSENCE, which is the one thing the
// other gates are all designed not to do.
//
// Deliberately biased toward silence. It reports only when a threshold is crossed AND there was
// work to do — an idle weekend must never open an issue, or this becomes the check people mute.

import { readFileSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

const argv = process.argv.slice(2);
const flag = (n) => argv.includes(n);
const value = (n) => {
  const i = argv.indexOf(n);
  return i === -1 ? undefined : argv[i + 1];
};

const DO_REPORT = flag('--report');
const FIXTURE = value('--fixture');

const gh = (args) => {
  const r = spawnSync('gh', args, { encoding: 'utf8', shell: process.platform === 'win32' });
  if (r.status !== 0) throw new Error(`gh ${args.join(' ')} failed:\n${r.stderr || r.stdout}`);
  return r.stdout;
};
const ghSoft = (args) => {
  const r = spawnSync('gh', args, { encoding: 'utf8', shell: process.platform === 'win32' });
  return { ok: r.status === 0, out: (r.stderr || r.stdout || '').trim() };
};

// Thresholds. Generous on purpose — a false alarm costs more than a late one, because the whole
// value of this file is that a human believes it when it fires.
const cfg = existsSync('workflow.json') ? JSON.parse(readFileSync('workflow.json', 'utf8')) : {};
const T = {
  queuedMinutes: 45, // GitHub schedules within minutes; 45 is starvation, not load
  reviewerFailures: 3, // one flake is noise, three in a row is a broken provider
  approvedPrHours: 3, // an approved PR the merger never took means the merger is stuck
  prAgeDays: 4, // a pull request nobody resolved in four days is not "in flight"
  ...(cfg.heartbeat ?? {}),
};

const MARKER = '<!-- plenipo-agent kind=heartbeat -->';
const TITLE = 'Loop health: the automation has stopped producing';

// ── The assessment, as a pure function so fixtures can prove it red ───────────
// Takes a snapshot of the world and returns findings. No clock, no network: `now` is passed in so
// a fixture can pin it, and every threshold comparison is arithmetic on that.
export function assess(state, now) {
  const findings = [];
  const mins = (iso) => (now - Date.parse(iso)) / 60000;

  // 1. Nothing is picking jobs up. The one that hides best: no run FAILS, they just never start.
  const stuck = (state.queuedRuns ?? []).filter((r) => mins(r.created_at) > T.queuedMinutes);
  if (stuck.length) {
    const oldest = Math.round(Math.max(...stuck.map((r) => mins(r.created_at))));
    findings.push({
      key: 'runners_starved',
      what: `${stuck.length} workflow run(s) queued, oldest ${oldest} min`,
      why: 'no runner is picking jobs up. Nothing goes red — the runs simply never start, so every '
        + 'gate downstream is waiting on evidence that will never arrive.',
      fix: 'Check https://www.githubstatus.com and the repo\'s Actions tab. Nothing local will fix it.',
    });
  }

  // 2. The reviewer is down. The merger refuses to merge without `agent:approved`, and only the
  //    reviewer can apply it — so this silently converts the whole queue into a no-op.
  const rv = (state.reviewerRuns ?? []).slice(0, T.reviewerFailures);
  if (rv.length >= T.reviewerFailures && rv.every((r) => r.conclusion === 'failure')) {
    findings.push({
      key: 'reviewer_down',
      what: `the approval reviewer failed its last ${rv.length} runs`,
      why: 'nothing can earn `agent:approved`, and `merge-gate.mjs` refuses to merge without it. '
        + 'The merger keeps reporting a clean run, because "no pull request is ready" is its '
        + 'normal healthy output.',
      fix: 'Open the newest failed run. A provider `HTTP 403` means the model credential, not the code.',
    });
  }

  // 3. An approved pull request the merger never took. Distinguishes "nothing to merge" (fine)
  //    from "something to merge and it did not happen" (not fine) — the ambiguity that hid the
  //    GITHUB_TOKEN deadlock for a week.
  const ripe = (state.openPrs ?? []).filter(
    (p) => (p.labels ?? []).includes('agent:approved') && mins(p.updated_at) / 60 > T.approvedPrHours
  );
  if (ripe.length) {
    findings.push({
      key: 'merger_stalled',
      what: `${ripe.length} approved PR(s) untouched for over ${T.approvedPrHours}h: `
        + ripe.map((p) => `#${p.number}`).join(', '),
      why: 'the merger runs every 15 minutes and has declined these every time. Its own runs are '
        + 'green, so nothing else will ever surface this.',
      fix: 'Run `node .github/scripts/merge-gate.mjs --pr <n>` — it prints the exact failing gate.',
    });
  }

  // 4. Pull requests nobody resolved. Not urgent, but it is the shape of a loop producing work
  //    faster than anything consumes it.
  const rotten = (state.openPrs ?? []).filter((p) => mins(p.created_at) / 1440 > T.prAgeDays);
  if (rotten.length) {
    findings.push({
      key: 'prs_rotting',
      what: `${rotten.length} pull request(s) open longer than ${T.prAgeDays} days: `
        + rotten.map((p) => `#${p.number}`).join(', '),
      why: 'the loop is opening work faster than anything closes it. Each one is also a merge '
        + 'conflict getting more expensive.',
      fix: 'Either merge them, or `/plenipo:deliver` is running too often for the review capacity.',
    });
  }

  return findings;
}

// ── Read the world ───────────────────────────────────────────────────────────
function readState() {
  const runs = JSON.parse(gh(['run', 'list', '--limit', '60', '--json',
    'status,conclusion,createdAt,name,workflowName']));
  const prs = JSON.parse(gh(['pr', 'list', '--state', 'open', '--limit', '50', '--json',
    'number,createdAt,updatedAt,labels']));

  // Any workflow whose job is to produce the approval label. Matched by name so a repo can rename
  // or run more than one without editing this file.
  const REVIEWER = /approval|intent.?review|verdict/i;

  return {
    queuedRuns: runs.filter((r) => r.status === 'queued').map((r) => ({ created_at: r.createdAt })),
    reviewerRuns: runs
      .filter((r) => REVIEWER.test(r.workflowName ?? r.name ?? '') && r.status === 'completed')
      .map((r) => ({ conclusion: r.conclusion })),
    openPrs: prs.map((p) => ({
      number: p.number,
      created_at: p.createdAt,
      updated_at: p.updatedAt,
      labels: (p.labels ?? []).map((l) => l.name),
    })),
  };
}

// Everything below runs ONLY when this file is executed directly. Without the guard, importing
// `assess` for the self-test runs the whole CLI — including the `process.exit(0)` a few lines
// down, which killed the test process before a single assertion ran and reported success. A test
// that exits 0 having asserted nothing is worse than no test, because it is believed.
const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (!isMain) { /* imported for tests — export only */ } else main();

function main() {
const state = FIXTURE ? JSON.parse(readFileSync(FIXTURE, 'utf8')) : readState();
const now = FIXTURE && state.now ? Date.parse(state.now) : Date.now();
const findings = assess(state, now);

// ── Report ───────────────────────────────────────────────────────────────────
if (!findings.length) {
  console.log('OK — the loop is producing, or has nothing to produce.\n');
} else {
  console.log(`${findings.length} loop-health finding(s):\n`);
  for (const f of findings) console.log(`  ${f.key}: ${f.what}\n    ${f.why}\n    -> ${f.fix}\n`);
}

if (!DO_REPORT || FIXTURE) process.exit(0);

// One issue, upserted by a hidden marker, so an hourly schedule cannot fan out 24 duplicates a day.
const existing = JSON.parse(
  gh(['issue', 'list', '--state', 'open', '--search', 'kind=heartbeat in:body', '--limit', '5',
    '--json', 'number,body'])
).find((i) => (i.body ?? '').includes(MARKER));

if (!findings.length) {
  // Recovered. Closing it is what makes the issue trustworthy — one that lingers after the outage
  // is one nobody reads next time.
  if (existing) {
    ghSoft(['issue', 'close', String(existing.number), '--reason', 'completed',
      '--comment', 'Recovered — the loop is producing again.']);
    console.log(`closed #${existing.number} — recovered`);
  }
  process.exit(0);
}

const body = [
  MARKER,
  '',
  '**The loop has stopped producing.** Every gate in this repo answers *may this merge?*; none of',
  'them answers *is anything happening at all?* This is that check, and it has fired.',
  '',
  ...findings.flatMap((f) => [`### ${f.key}`, '', `**${f.what}**`, '', f.why, '', `**Try:** ${f.fix}`, '']),
  '---',
  '',
  'Updated automatically each run, and closed on its own once the loop recovers.',
].join('\n');

if (existing) {
  gh(['issue', 'edit', String(existing.number), '--body', body]);
  console.log(`updated #${existing.number}`);
} else {
  const out = gh(['issue', 'create', '--title', TITLE, '--body', body, '--label', 'needs-human']);
  console.log(`opened ${out.trim()}`);
}
process.exit(0);
}
