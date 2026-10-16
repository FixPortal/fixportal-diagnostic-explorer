"""Produce a bounded, pinned, non-deploying Bicep artifact; no Azure login needed."""
import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import stat
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request

VERSION = "0.46.1"
URL = "https://github.com/Azure/bicep/releases/download/v0.46.1/bicep-linux-x64"
BINARY_BYTES = 104933840
BINARY_SHA256 = "3e011d629ea4311b7a7dd8f0040ab2b1a072ea4ff5d02cb75e0e55a9a6703fb9"
DOWNLOAD_CAP = 112 * 1024 * 1024
TEMPLATE_CAP = 1024 * 1024
SOURCE = "infra/retro-loganalytics.bicep"
PUBLIC_HOSTS = {"github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"}


def public_url(url):
    parsed = urllib.parse.urlsplit(url)
    if (parsed.scheme != "https" or parsed.hostname not in PUBLIC_HOSTS
            or parsed.username or parsed.password or parsed.port not in (None, 443)):
        raise ValueError("Only the reviewed public HTTPS release hosts are permitted")
    return url


class PublicRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        public_url(newurl)
        # Build a new request, carrying no authentication/cookie/proxy headers.
        return urllib.request.Request(newurl, headers={"Accept": "application/octet-stream"})


@contextmanager
def acquisition_deadline(deadline):
    def expired(_signal, _frame):
        raise TimeoutError("Compiler acquisition deadline expired")
    previous = signal.signal(signal.SIGALRM, expired)
    signal.setitimer(signal.ITIMER_REAL, max(0.001, deadline - time.monotonic()))
    try:
        yield
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, previous)


def download(destination, deadline):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), PublicRedirect())
    request = urllib.request.Request(public_url(URL), headers={"Accept": "application/octet-stream"})
    digest = hashlib.sha256()
    count = 0
    with opener.open(request, timeout=max(0.1, min(30, deadline - time.monotonic()))) as response:
        public_url(response.url)
        length = response.headers.get("Content-Length")
        if length is not None and int(length) != BINARY_BYTES:
            raise ValueError("Official compiler Content-Length differs from reviewed size")
        with destination.open("xb") as output:
            while True:
                if time.monotonic() >= deadline:
                    raise TimeoutError("Compiler acquisition deadline expired")
                chunk = response.read(64 * 1024)
                if not chunk:
                    break
                count += len(chunk)
                if count > DOWNLOAD_CAP or count > BINARY_BYTES:
                    raise ValueError("Compiler response exceeds the reviewed byte cap")
                output.write(chunk)
                digest.update(chunk)
    if count != BINARY_BYTES or digest.hexdigest() != BINARY_SHA256:
        raise ValueError("Official compiler byte count or SHA256 differs from reviewed pin")


def run_bounded(arguments, seconds, deadline, log):
    """Linux process group, two independently drained capped binary streams."""
    remaining = min(seconds, deadline - time.monotonic())
    if remaining <= 0:
        raise TimeoutError("Shared compiler deadline expired")
    process = subprocess.Popen(arguments, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, start_new_session=True)
    buffers = [bytearray(), bytearray()]
    overflow = threading.Event()
    reader_errors = []

    def kill():
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass

    def drain(stream, output):
        try:
            while chunk := stream.read(4096):
                if len(output) + len(chunk) > 2 * 1024 * 1024:
                    overflow.set()
                    kill()
                    break
                output.extend(chunk)
        except Exception as error:
            reader_errors.append(str(error))
            kill()
        finally:
            stream.close()

    threads = [threading.Thread(target=drain, args=(stream, buffer), daemon=True)
               for stream, buffer in zip((process.stdout, process.stderr), buffers)]
    for thread in threads:
        thread.start()
    failure = None
    try:
        process.wait(timeout=remaining)
    except subprocess.TimeoutExpired:
        failure = TimeoutError("Bounded compiler process timed out")
        kill()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            failure = RuntimeError("Compiler group did not exit after bounded timeout cleanup")
    finally:
        drain_deadline = min(deadline + 5, time.monotonic() + 5)
        for thread in threads:
            thread.join(max(0, drain_deadline - time.monotonic()))
        # Retain capped raw diagnostics BEFORE classifying a failure.
        log.with_suffix(".stdout.log").write_bytes(buffers[0])
        log.with_suffix(".stderr.log").write_bytes(buffers[1])
    failed = failure or overflow.is_set() or reader_errors or any(thread.is_alive() for thread in threads)
    if failed or process.returncode:
        # The hosted job log remains useful even when compilation cannot produce its success artifact.
        for label, data in zip(("stdout", "stderr"), buffers):
            sys.stderr.write("\nBicep failure diagnostic " + label + " (last at most 16384 bytes):\n")
            sys.stderr.write(bytes(data[-16384:]).decode("utf-8", errors="replace") + "\n")
    if failed:
        kill()
        raise failure or RuntimeError("Compiler output overflow or incomplete bounded drain")
    if process.returncode:
        raise RuntimeError("Compiler command failed; bounded failure diagnostics emitted")
    return bytes(buffers[0])


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("Duplicate compiled JSON key: " + key)
        value[key] = item
    return value


def template_envelope(data):
    if not data or len(data) > TEMPLATE_CAP:
        raise ValueError("Compiled template is empty or exceeds its one MiB cap")
    document = json.loads(data.decode("utf-8"), object_pairs_hook=unique_object)
    if not isinstance(document, dict) or not document:
        raise ValueError("Compiled template must be a nonempty JSON object")
    return document


def regular(path):
    if not stat.S_ISREG(path.lstat().st_mode):
        raise ValueError("Expected an ordinary, unlinked file: " + str(path))


def compile_artifact(repo, runner_temp):
    if os.name != "posix":
        raise RuntimeError("This reviewed compiler lane is Linux only")
    deadline = time.monotonic() + 150
    repo = repo.resolve(strict=True)
    runner_temp = runner_temp.resolve(strict=True)
    work = runner_temp / "diag-retro-compile"
    work.mkdir()  # Refuse existing stages; no overwrite or recursive cleanup.
    artifacts = work / "artifacts"
    artifacts.mkdir()
    source = repo / SOURCE
    regular(source)
    if source.resolve() != source:
        raise ValueError("Linked Bicep source parents are not supported")
    before = source.read_bytes()
    if not before or len(before) > 1024 * 1024:
        raise ValueError("Unexpected Bicep source size")
    compiler = work / "bicep-linux-x64"
    download_deadline = min(deadline, time.monotonic() + 30)
    with acquisition_deadline(download_deadline):
        download(compiler, download_deadline)
    regular(compiler)
    # Verify file bytes once more immediately before enabling executable permission.
    with compiler.open("rb") as verified:
        actual_digest = hashlib.file_digest(verified, "sha256").hexdigest()
    if compiler.stat().st_size != BINARY_BYTES or actual_digest != BINARY_SHA256:
        raise ValueError("Compiler changed after acquisition")
    compiler.chmod(0o700)
    version = run_bounded([str(compiler), "--version"], 20, deadline, work / "version")
    if len(version) > 4096 or not re.fullmatch(rb"Bicep CLI version 0\.46\.1 \([^\r\n]+\)\r?\n?", version):
        raise ValueError("Actual compiler version differs from reviewed release")
    (artifacts / "compiler-version.txt").write_bytes(version)
    output = artifacts / "retro-loganalytics.json"
    run_bounded([str(compiler), "build", "--no-restore", str(source), "--outfile", str(output)],
                60, deadline, work / "build")
    regular(output)
    if output.stat().st_size > TEMPLATE_CAP:
        raise ValueError("Compiled template exceeds its envelope cap")
    data = output.read_bytes()
    template_envelope(data)  # No selected-resource/column shape assumptions in phase one.
    if source.read_bytes() != before:
        raise ValueError("Tracked Bicep source changed during compilation")
    head = run_bounded(["git", "-C", str(repo), "rev-parse", "HEAD"], 10, deadline, work / "source-head").decode().strip()
    if not re.fullmatch(r"[0-9a-f]{40}", head):
        raise ValueError("Invalid source commit provenance")
    # Tie the input bytes to the actual checked-out commit, not a modified working copy.
    tracked = run_bounded(["git", "-C", str(repo), "show", "HEAD:" + SOURCE],
                          10, deadline, work / "tracked-source")
    if tracked != before:
        raise ValueError("Bicep input differs from the recorded source commit")
    provenance = {"phase": 1, "sourceCommit": head, "source": SOURCE,
                  "sourceBytes": len(before), "sourceSHA256": hashlib.sha256(before).hexdigest(),
                  "compilerVersion": version.decode().strip(), "compilerURL": URL,
                  "compilerBytes": BINARY_BYTES, "compilerSHA256": BINARY_SHA256,
                  "templateBytes": len(data), "templateSHA256": hashlib.sha256(data).hexdigest(),
                  "scope": "Offline compilation only; no deployment or schema-consumer assertion"}
    (artifacts / "compiler-provenance.json").write_text(json.dumps(provenance, indent=2), encoding="utf-8")
    if {path.name for path in artifacts.iterdir()} != {
            "compiler-version.txt", "compiler-provenance.json", "retro-loganalytics.json"}:
        raise ValueError("Unexpected compilation artifact set")
    print("Pinned non-deploying Bicep compilation artifacts: " + str(artifacts))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--runner-temp", type=Path, required=True)
    arguments = parser.parse_args()
    compile_artifact(arguments.repo, arguments.runner_temp)
