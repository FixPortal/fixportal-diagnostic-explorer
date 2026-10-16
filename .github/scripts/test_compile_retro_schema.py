"""Offline fixtures only: no real compiler, download, cloud, or subprocess execution."""
import copy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import compile_retro_schema as compiler
from retro_schema_workflow import Invalid, read_yaml

ROOT = Path(__file__).resolve().parents[2]
COMMAND = 'python3 .github/scripts/compile_retro_schema.py --repo "$GITHUB_WORKSPACE" --runner-temp "$RUNNER_TEMP"'
ARTIFACTS = "\n".join('${{ runner.temp }}/diag-retro-compile/artifacts/' + name for name in
                       ('compiler-version.txt', 'retro-loganalytics.json', 'compiler-provenance.json')) + "\n"
REQUIRED = {'.github/scripts/compile_retro_schema.py', '.github/scripts/retro_schema_workflow.py',
            '.github/scripts/test_compile_retro_schema.py'}


def validate_wiring(document, policy):
    job = document['jobs']['backend']
    if ('if' in job or 'continue-on-error' in job or job['runs-on'] != 'ubuntu-latest'
            or job['timeout-minutes'] != '10'):
        raise Invalid('existing required backend runner and budget must be preserved')
    needs = document['jobs']['ci-gate']['needs']
    if not isinstance(needs, str) or 'backend' not in [v.strip() for v in needs.strip('[]').split(',')]:
        raise Invalid('backend must feed the required gate')
    steps = job['steps']
    def one(name):
        found = [(index, step) for index, step in enumerate(steps)
                 if isinstance(step, dict) and step.get('name') == name]
        if len(found) != 1:
            raise Invalid('exactly one executable step required: ' + name)
        return found[0]
    compile_i, compile_step = one('Compile pinned Retro deployment template')
    if compile_step != {'name': 'Compile pinned Retro deployment template', 'timeout-minutes': '3', 'run': COMMAND}:
        raise Invalid('unconditional pinned compiler command and bounded step required')
    upload_i, upload = one('Retain compiled Retro deployment template')
    if upload != {'name': 'Retain compiled Retro deployment template', 'uses': 'actions/upload-artifact@v7',
                  'with': {'name': 'retro-compiled-template', 'if-no-files-found': 'error', 'path': ARTIFACTS}}:
        raise Invalid('exact three artifacts must be retained without optional conditions')
    build_i, _ = one('Build')
    if not compile_i < upload_i < build_i:
        raise Invalid('compile and retain actual template before ordinary build/test')
    if not REQUIRED.issubset(policy['high']):
        raise Invalid('new executable compiler/guard controls must remain HIGH')


class WiringTests(unittest.TestCase):
    def setUp(self):
        self.text = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
        self.document = read_yaml(self.text)
        self.policy = json.loads((ROOT / '.claude/review-policy.json').read_text(encoding='utf-8'))

    def test_actual_required_phase_one_wiring(self):
        validate_wiring(self.document, self.policy)

    def test_executable_omissions_and_optional_steps_fail_closed(self):
        for name in ('Compile pinned Retro deployment template', 'Retain compiled Retro deployment template'):
            for mutation in ('omit', 'conditional', 'continue', 'duplicate'):
                with self.subTest(name=name, mutation=mutation):
                    document = copy.deepcopy(self.document)
                    steps = document['jobs']['backend']['steps']
                    step = next(s for s in steps if s.get('name') == name)
                    if mutation == 'omit':
                        steps.remove(step)
                    elif mutation == 'duplicate':
                        steps.append(copy.deepcopy(step))
                    else:
                        step['if' if mutation == 'conditional' else 'continue-on-error'] = 'true'
                    with self.assertRaises(Invalid):
                        validate_wiring(document, self.policy)

    def test_wrong_command_artifact_order_and_high_fail_closed(self):
        for mutation in ('command', 'partial-artifact', 'order', 'high', 'budget', 'gate'):
            with self.subTest(mutation=mutation):
                document, policy = copy.deepcopy(self.document), copy.deepcopy(self.policy)
                job = document['jobs']['backend']
                compile_step = next(s for s in job['steps'] if s.get('name') == 'Compile pinned Retro deployment template')
                upload = next(s for s in job['steps'] if s.get('name') == 'Retain compiled Retro deployment template')
                if mutation == 'command':
                    compile_step['run'] = 'echo ' + COMMAND
                elif mutation == 'partial-artifact':
                    upload['with']['path'] = ARTIFACTS.splitlines()[0]
                elif mutation == 'order':
                    job['steps'].remove(compile_step)
                    job['steps'].append(compile_step)
                elif mutation == 'high':
                    policy['high'].remove('.github/scripts/compile_retro_schema.py')
                elif mutation == 'budget':
                    job['timeout-minutes'] = '20'
                else:
                    document['jobs']['ci-gate']['needs'] = '[frontend, gate-coverage]'
                with self.assertRaises(Invalid):
                    validate_wiring(document, policy)

    def test_observed_folded_command_and_literal_artifact_chomping(self):
        self.assertEqual(read_yaml('run: >-\n  first command\n  next argument\n\nnext: value\n')['run'],
                         'first command next argument')
        self.assertEqual(read_yaml('path: |\n  one.txt\n  two.json\n\nnext: value\n')['path'],
                         'one.txt\ntwo.json\n')
        self.assertEqual(read_yaml('path: |-\n  one.txt\n  two.json\n\nnext: value\n')['path'],
                         'one.txt\ntwo.json')
        self.assertEqual(read_yaml('run: >\n  first\n  second\n')['run'], 'first second\n')

    def test_unsupported_folded_paragraphs_and_indented_folding_fail_closed(self):
        for text in ('run: >-\n  first\n\n  paragraph\n', 'run: >-\n  first\n    more indented\n'):
            with self.subTest(text=text), self.assertRaises(Invalid):
                read_yaml(text)

    def test_duplicate_keys_and_unsupported_yaml_are_rejected(self):
        with self.assertRaises(Invalid):
            read_yaml('jobs:\n  backend: 1\n  backend: 2\n')
        with self.assertRaises(Invalid):
            read_yaml('jobs: &alias\n  backend: *alias\n')


class EnvelopeTests(unittest.TestCase):
    def test_exact_reviewed_compiler_pin(self):
        self.assertEqual(compiler.VERSION, '0.46.1')
        self.assertEqual(compiler.URL, 'https://github.com/Azure/bicep/releases/download/v0.46.1/bicep-linux-x64')
        self.assertEqual(compiler.BINARY_BYTES, 104933840)
        self.assertEqual(compiler.BINARY_SHA256, '3e011d629ea4311b7a7dd8f0040ab2b1a072ea4ff5d02cb75e0e55a9a6703fb9')

    def test_envelope_is_shape_agnostic_but_not_empty_duplicate_or_malformed(self):
        self.assertEqual(compiler.template_envelope(b'{"resources":[]}'), {'resources': []})
        self.assertEqual(compiler.template_envelope(b'{"resources":{}}'), {'resources': {}})
        for data in (b'', b'[]', b'{}', b'{', b'{"x":1,"x":2}', b'\xff'):
            with self.subTest(data=data), self.assertRaises((ValueError, UnicodeError)):
                compiler.template_envelope(data)
        with patch.object(compiler, 'TEMPLATE_CAP', 2), self.assertRaises(ValueError):
            compiler.template_envelope(b'{"x":1}')

    def test_redirects_refuse_credentials_http_and_unreviewed_hosts(self):
        for url in ('http://github.com/a', 'https://user:pass@github.com/a', 'https://example.com/a',
                    'https://github.com:444/a', 'https://github.com.evil.test/a'):
            with self.subTest(url=url), self.assertRaises(ValueError):
                compiler.public_url(url)
        self.assertEqual(compiler.public_url('https://release-assets.githubusercontent.com/a'),
                         'https://release-assets.githubusercontent.com/a')

    def test_actual_generated_fixture_has_current_source_and_tool_lineage(self):
        fixture = (ROOT / compiler.FIXTURE).read_bytes()
        lineage = json.loads((ROOT / compiler.LINEAGE).read_text(encoding='utf-8'))
        source = (ROOT / compiler.SOURCE).read_bytes()
        compiler.verify_generated_fixture(fixture, lineage, fixture, source, lineage['compilerVersion'])

    def test_fresh_output_source_and_pin_drift_are_rejected(self):
        fixture = (ROOT / compiler.FIXTURE).read_bytes()
        lineage = json.loads((ROOT / compiler.LINEAGE).read_text(encoding='utf-8'))
        source = (ROOT / compiler.SOURCE).read_bytes()
        for mutation in ('output', 'source', 'pin', 'version', 'missing-lineage'):
            with self.subTest(mutation=mutation):
                changed = dict(lineage)
                actual, current_source, version = fixture, source, lineage['compilerVersion']
                if mutation == 'output':
                    actual += b' '
                elif mutation == 'source':
                    current_source += b' '
                elif mutation == 'pin':
                    changed['compilerSHA256'] = '0' * 64
                elif mutation == 'version':
                    version = 'Bicep CLI version 0.0.0 (fixture)'
                else:
                    changed.pop('sourceSHA256')
                with self.assertRaises(ValueError):
                    compiler.verify_generated_fixture(fixture, changed, actual, current_source, version)

    def test_nonzero_compiler_emits_bounded_retained_failure_diagnostics_without_a_child(self):
        class Process:
            stdout = io.BytesIO(b"x" * 17000 + b"actual-synthetic-compiler-diagnostic")
            stderr = io.BytesIO(b"specific Bicep error")
            returncode = 1
            pid = 123456789
            def wait(self, timeout):
                return self.returncode
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / 'fixture-only'
            output = io.StringIO()
            with patch.object(compiler.subprocess, 'Popen', return_value=Process()) as start, \
                    patch.object(compiler.sys, 'stderr', output), patch.object(compiler.os, 'killpg'):
                with self.assertRaisesRegex(RuntimeError, 'bounded failure diagnostics emitted'):
                    compiler.run_bounded(['not-an-executable-fixture'], 1, compiler.time.monotonic() + 5, log)
            self.assertIn('actual-synthetic-compiler-diagnostic', output.getvalue())
            self.assertIn('specific Bicep error', output.getvalue())
            self.assertLess(len(output.getvalue()), 2 * 16384 + 1024)
            self.assertEqual(log.with_suffix('.stderr.log').read_bytes(), b'specific Bicep error')
            self.assertTrue(start.call_args.kwargs['start_new_session'])

    def test_streamed_digest_size_and_proxy_refusal_without_network(self):
        class Response(io.BytesIO):
            url = compiler.URL
            headers = {'Content-Length': '3'}
        class Opener:
            def open(self, request, timeout):
                self.request = request
                return Response(b'abc')
        for expected_size, expected_hash, succeeds in (
                (3, hashlib.sha256(b'abc').hexdigest(), True), (3, '0' * 64, False), (2, '0' * 64, False)):
            with self.subTest(size=expected_size, digest=expected_hash), tempfile.TemporaryDirectory() as directory:
                with patch.multiple(compiler, BINARY_BYTES=expected_size, BINARY_SHA256=expected_hash), \
                        patch.object(compiler.urllib.request, 'build_opener', return_value=Opener()) as opener:
                    output = Path(directory) / 'owned-fixture-only'
                    if succeeds:
                        compiler.download(output, compiler.time.monotonic() + 10)
                        self.assertEqual(output.read_bytes(), b'abc')
                    else:
                        with self.assertRaises(ValueError):
                            compiler.download(output, compiler.time.monotonic() + 10)
                    self.assertEqual(opener.call_args.args[0].proxies, {})


if __name__ == '__main__':
    unittest.main()
