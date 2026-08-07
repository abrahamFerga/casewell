#!/usr/bin/env node
// Self-test for loop-heartbeat.mjs. No network, no `gh`, no issues opened.
//
//   node .github/scripts/loop-heartbeat.test.mjs
//
// The heartbeat's whole job is to fire on ABSENCE — nothing queued moving, nothing merging — which
// is the one condition no other gate here reports. That makes it the easiest check in the repo to
// ship broken: a detector that never fires looks identical to a healthy loop. So each case below
// pins a threshold in BOTH directions, and the last one asserts silence, because a heartbeat that
// cries wolf on an idle weekend is one somebody mutes.

import { assess } from './loop-heartbeat.mjs';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const state = JSON.parse(readFileSync(join(here, 'fixtures', 'heartbeat.json'), 'utf8'));
const NOW = Date.parse(state.now);

let failed = 0;
const check = (name, ok, why) => {
  console.log(ok ? `  ok   ${name} — ${why}` : `  FAIL ${name} — ${why}`);
  if (!ok) failed++;
};

const keys = (s, now = NOW) => assess(s, now).map((f) => f.key);

// ── Fires on the real outage this file was written from ──────────────────────
const all = keys(state);

check('runners_starved', all.includes('runners_starved'),
  'two runs queued past 45 min must fire: no runner is taking jobs, and nothing goes red');

check('reviewer_down', all.includes('reviewer_down'),
  'three consecutive reviewer failures must fire: nothing can earn agent:approved, so nothing merges');

check('merger_stalled', all.includes('merger_stalled'),
  'an approved PR untouched for 9h must fire: the merger declined it every 15 min while staying green');

check('prs_rotting', all.includes('prs_rotting'),
  'a PR open 9 days must fire: work is being produced faster than anything consumes it');

// ── And stays silent when it should ──────────────────────────────────────────
// The direction that matters most. Every threshold here is crossed downward, so a detector that
// fires unconditionally — the easiest way to ship this broken — fails.
const healthy = {
  queuedRuns: [{ created_at: '2026-08-06T17:58:00Z' }], // 2 min: normal scheduling
  reviewerRuns: [{ conclusion: 'success' }, { conclusion: 'failure' }, { conclusion: 'failure' }],
  openPrs: [
    { number: 810, created_at: '2026-08-06T17:00:00Z', updated_at: '2026-08-06T17:50:00Z', labels: ['agent:approved'] },
  ],
};
check('silent when healthy', keys(healthy).length === 0,
  'a 2-min queue, a reviewer that recovered, and a 10-min-old approved PR must report nothing');

// An idle repo is the most common state overnight, and the one a noisy heartbeat ruins.
check('silent when idle', keys({ queuedRuns: [], reviewerRuns: [], openPrs: [] }).length === 0,
  'nothing queued, nothing open, nothing reviewed — an idle loop is healthy, not broken');

// A reviewer that failed twice and then succeeded is a flake, not an outage. Ordering matters:
// `reviewerRuns` is newest-first, so a success at the head must clear it.
check('one recovery clears reviewer_down',
  !keys({ ...state, reviewerRuns: [{ conclusion: 'success' }, { conclusion: 'failure' }, { conclusion: 'failure' }] })
    .includes('reviewer_down'),
  'newest-first: a success at the head means the provider is back, whatever came before it');

if (failed) {
  console.log(`\n${failed} heartbeat case(s) wrong. This is the only check that reports a stopped loop — do not merge this.\n`);
  process.exit(1);
}
console.log('\nOK — the heartbeat fires on absence and stays quiet otherwise.\n');
