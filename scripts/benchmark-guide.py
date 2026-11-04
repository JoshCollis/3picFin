"""Run alternating native/candidate browser replays and summarize public numeric metrics only."""
import argparse
import json
import math
import os
import re
from pathlib import Path
import statistics
import subprocess
import sys

parser = argparse.ArgumentParser()
parser.add_argument('--evidence', type=Path, required=True)
parser.add_argument('--runs', type=int, default=3, choices=range(1, 6))
parser.add_argument('--summarize-only', action='store_true')
parser.add_argument('--asset', type=Path, help='Candidate asset; defaults to evidence/guide-patched.js')
parser.add_argument('--prefix', default='ab', help='Separate revision outputs without replacing prior evidence')
args = parser.parse_args()
if not re.fullmatch(r'[A-Za-z0-9_-]+', args.prefix):
    parser.error('Prefix must contain only letters, digits, underscores or hyphens')
os.umask(0o077)
root = args.evidence.resolve()
repo = Path(__file__).resolve().parents[1]
if root.is_relative_to(repo):
    parser.error('Keep private benchmark evidence outside the repository')
if not args.summarize_only:
    for index in range(1, args.runs + 1):
        for variant in (['native', 'candidate'] if index % 2 else ['candidate', 'native']):
            command = [sys.executable, 'tests/guide-native.browser.py', '--dataset', str(root/'frozen-guide.json'),
                       '--output', str(root/f'{args.prefix}-{index}-{variant}')]
            if variant == 'candidate':
                command += ['--asset', str(args.asset or root/'guide-patched.js'), '--assert-bounded']
            with (root/f'{args.prefix}-{index}-{variant}.log').open('w') as log:
                subprocess.run(command, cwd=repo, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=120)


def percentile(values, fraction):
    return sorted(values)[max(0, math.ceil(len(values)*fraction)-1)]


results = []
for index in range(1, args.runs + 1):
    for variant in ('native', 'candidate'):
        data = json.loads((root/f'{args.prefix}-{index}-{variant}/benchmark.json').read_text())
        first, scroll = data['first'], data['scroll']
        programs = [r for r in data['requests'] if r['kind'] == 'programmes']
        assert len(programs) == 1, 'Unexpected duplicate initial programme queries'
        assert data['catalogue'] >= 1879 and not data['errors']
        results.append({'run': index, 'variant': variant, 'datasetSha256': data['datasetSha256'],
            'browserVersion': data['browserVersion'], 'rows': first['rows'], 'cells': first['cells'], 'nodes': first['nodes'],
            'navigationToUsableMs': first['usableMs'], 'channelRequestToUsableMs': first['channelRequestToUsableMs'],
            'programmeResponseToUsableMs': first['programmeResponseToUsableMs'], 'localFixtureApiMs': first['fixtureApiMs'],
            'initialGuideLongTaskMs': sum(e['duration'] for e in first['longTasks']),
            'scrollFrames': len(scroll['frames']), 'scrollFrameP50Ms': percentile(scroll['frames'], .5),
            'scrollElapsedMs': scroll['elapsedMs'],
            'scrollFrameP95Ms': percentile(scroll['frames'], .95), 'scrollMaxFrameMs': max(scroll['frames']),
            'scrollLongTasks': len(scroll['longTasks']), 'scrollLongTaskMs': sum(e['duration'] for e in scroll['longTasks']),
            'scrollCpuSeconds': data['scrollCpuSeconds'], 'verticalDistance': scroll['verticalDistance'],
            'horizontalDistance': scroll['maxHorizontal'], 'programmeQuery': programs[0]})
assert len({r['datasetSha256'] for r in results}) == 1
assert len({r['verticalDistance'] for r in results}) == 1, 'Vertical scroll path was not like-for-like'
assert len({r['horizontalDistance'] for r in results}) == 1, 'Time scroll path was not like-for-like'
assert len({(r['programmeQuery']['minEnd'], r['programmeQuery']['maxStart']) for r in results}) == 1, 'Guide time windows differed'
summary = {'scope': 'native served web + fixed real-data replay + synthetic identity; no live-user/Wolf/AndroidTV claim',
           'method': '1280x850 UTC; native performance/timers; advancing wall-time anchor; 4x CPU; warm local public assets; '
                     '4-second first-ten-row vertical + full-day horizontal pan, two-frame drain; alternating A/B; no production writes',
           'results': results, 'medians': {}}
for variant in ('native', 'candidate'):
    selected = [r for r in results if r['variant'] == variant]
    summary['medians'][variant] = {key: statistics.median(r[key] for r in selected) for key in (
        'navigationToUsableMs', 'channelRequestToUsableMs', 'programmeResponseToUsableMs', 'initialGuideLongTaskMs',
        'scrollFrameP50Ms', 'scrollFrameP95Ms', 'scrollMaxFrameMs', 'scrollLongTasks', 'scrollLongTaskMs', 'nodes', 'cells', 'rows')}
(root/('benchmark-summary.json' if args.prefix=='ab' else f'{args.prefix}-summary.json')).write_text(json.dumps(summary, indent=2))
print(json.dumps(summary['medians'], indent=2))
