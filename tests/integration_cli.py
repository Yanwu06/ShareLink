#!/usr/bin/env python3
"""Exercise the actual shared Rust engine through two independent CLI processes.

Usage: python tests/integration_cli.py target/debug/sharelink-cli.exe
Uses only Python's standard library. All files and identities are temporary.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import queue
import socket
import subprocess
import tempfile
import threading
import time
from datetime import datetime


TERMINAL = {"completed", "failed", "cancelled", "rejected"}


class TransferProxy:
    """Forward one encrypted connection; optionally interrupt its outgoing data."""
    def __init__(self, target_port: int, drop_after: int | None = None, upload_delay: float = 0):
        self.target_port = target_port
        self.drop_after = drop_after
        self.upload_delay = upload_delay
        self.upload_bytes = 0
        self.sockets = []
        self.stopping = threading.Event()
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen(1)
        self.port = self.listener.getsockname()[1]
        self.thread = threading.Thread(target=self._serve, daemon=True)
        self.thread.start()

    def _serve(self):
        try:
            client, _ = self.listener.accept()
            server = socket.create_connection(("127.0.0.1", self.target_port), timeout=5)
            server.settimeout(None)
            self.sockets = [client, server]
            reverse = threading.Thread(target=self._pump, args=(server, client, False), daemon=True)
            reverse.start()
            self._pump(client, server, True)
            reverse.join(timeout=5)
        except OSError:
            pass

    def _pump(self, source, target, upload):
        try:
            while not self.stopping.is_set():
                data = source.recv(64 * 1024)
                if not data:
                    break
                if upload and self.drop_after is not None:
                    remaining = self.drop_after - self.upload_bytes
                    data = data[:remaining]
                if upload and self.upload_delay:
                    time.sleep(self.upload_delay)
                target.sendall(data)
                if upload:
                    self.upload_bytes += len(data)
                    if self.drop_after is not None and self.upload_bytes >= self.drop_after:
                        break
        except OSError:
            pass
        finally:
            self.close()

    def close(self):
        self.stopping.set()
        for connection in self.sockets + [self.listener]:
            try:
                connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            connection.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
        self.thread.join(timeout=5)


class Node:
    def __init__(self, executable: Path, root: Path, name: str):
        self.executable = executable
        self.root = root
        self.name = name
        self.downloads = root / "downloads"
        self.downloads.mkdir(parents=True, exist_ok=True)
        self.config = root / "config.json"
        self.config.write_text(json.dumps({
            "name": name,
            "download_dir": str(self.downloads),
            "state_dir": str(root / "state"),
            "port": 0,
            "discovery_port": 0,
        }), encoding="utf-8")
        self.process = None
        self.start()

    def start(self):
        self.responses = queue.Queue()
        self.errors = []
        self.process = subprocess.Popen(
            [str(self.executable), str(self.config)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            encoding="utf-8", bufsize=1,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        self.reader_threads = [
            threading.Thread(target=self._read_stdout, args=(self.process.stdout, self.responses), daemon=True),
            threading.Thread(target=self._read_stderr, args=(self.process.stderr, self.errors), daemon=True),
        ]
        for reader in self.reader_threads:
            reader.start()
        self.info = self.call({"op": "snapshot"})["info"]

    def _read_stdout(self, stream, responses):
        for line in stream:
            responses.put(line)
        responses.put(None)

    def _read_stderr(self, stream, errors):
        for line in stream:
            errors.append(line.rstrip())

    def call(self, request: dict, require_ok: bool = True) -> dict:
        assert self.process and self.process.poll() is None, self.errors
        self.process.stdin.write(json.dumps(request, ensure_ascii=False) + "\n")
        self.process.stdin.flush()
        try:
            line = self.responses.get(timeout=15)
        except queue.Empty as error:
            raise AssertionError(f"{self.name} API timed out: {request}; {self.errors}") from error
        assert line is not None, f"{self.name} exited: {self.errors}"
        response = json.loads(line)
        if require_ok:
            assert response.get("ok"), f"{self.name}: {request}: {response}"
        return response

    def transfers(self) -> list[dict]:
        return self.call({"op": "snapshot"})["transfers"]

    def stop(self):
        if self.process is None:
            return
        if self.process.poll() is None:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        for reader in self.reader_threads:
            reader.join(timeout=5)
        for stream in (self.process.stdout, self.process.stderr):
            stream.close()


def wait_for(predicate, description: str, timeout: float = 20):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(0.04)
    raise AssertionError(f"Timed out waiting for {description}")


def find_transfer(node: Node, transfer_id: str):
    return next((t for t in node.transfers() if t["id"] == transfer_id), None)


def terminal(node: Node, transfer_id: str, timeout: float = 30):
    def ready():
        transfer = find_transfer(node, transfer_id)
        return transfer if transfer and transfer["state"] in TERMINAL else None
    return wait_for(ready, f"{node.name} transfer {transfer_id} to finish", timeout)


def incoming(node: Node, before: set[str]):
    def ready():
        return next((t for t in node.transfers()
                     if t["direction"] == "receive" and t["id"] not in before), None)
    return wait_for(ready, f"incoming request on {node.name}")


def send(sender: Node, receiver: Node, files: list[dict], *, code=None, fingerprint=None, connect_port=None):
    peer = sender.call({"op": "probe", "host": "127.0.0.1", "port": receiver.info["port"]})["peer"]
    assert peer["fingerprint"] == receiver.info["fingerprint"]
    if fingerprint is not None:
        peer["fingerprint"] = fingerprint
    if connect_port is not None:
        peer["port"] = connect_port
    return sender.call({
        "op": "send", "peer": peer,
        "verificationCode": code if code is not None else receiver.info["verificationCode"],
        "files": files,
    }, require_ok=False)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while True:
            chunk = stream.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def run(executable: Path, throughput_mib: int = 128, report_path: Path = None):
    report = {"startedAt": datetime.now().astimezone().isoformat(),
              "executable": str(executable), "executableSha256": sha256(executable),
              "environment": {"system": platform.system(), "release": platform.release(),
                              "version": platform.version(), "machine": platform.machine(),
                              "pythonVersion": platform.python_version()},
              "checks": [], "status": "running"}
    def passed(name, **details):
        report["checks"].append({"name": name, "status": "passed", **details})
        print("PASS: " + name, flush=True)
    def save_report():
        if report_path is not None:
            report_path.parent.mkdir(parents=True, exist_ok=True)
            report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    save_report()
    with tempfile.TemporaryDirectory(prefix="sharelink-integration-") as temporary:
        root = Path(temporary)
        sender = Node(executable, root / "sender", "发送电脑")
        receiver = None
        try:
            receiver = Node(executable, root / "receiver", "接收手机")
            source = root / "source"
            source.mkdir()
            payload = source / "资料-照片-😀.bin"
            payload.write_bytes(os.urandom(8 * 1024 * 1024 + 137))
            empty = source / "空文件.txt"
            empty.write_bytes(b"")
            files = [{"path": str(p), "name": p.name} for p in (payload, empty)]

            # A user must see and accept an offer before content is stored.
            before = {t["id"] for t in receiver.transfers()}
            offered = send(sender, receiver, files)
            assert offered.get("ok"), offered
            request = incoming(receiver, before)
            assert request["state"] == "awaiting_accept", request
            assert not (receiver.downloads / payload.name).exists()
            assert not (receiver.downloads / ".sharelink-partials").exists()
            assert all("path" not in item for item in request["files"]), request
            receiver.call({"op": "accept", "id": request["id"]})
            sent = terminal(sender, offered["id"])
            received = terminal(receiver, request["id"])
            assert sent["state"] == received["state"] == "completed", (sent, received)
            assert sha256(payload) == sha256(receiver.downloads / payload.name)
            assert (receiver.downloads / empty.name).stat().st_size == 0
            passed("two-process encrypted transfer, Chinese/emoji names, zero bytes, exact digest")

            # Existing destination files must never be silently replaced.
            marker = b"Existing file owned by the receiver; do not overwrite."
            collision = receiver.downloads / "existing.txt"
            collision.write_bytes(marker)
            before = {t["id"] for t in receiver.transfers()}
            offered = send(sender, receiver, [{"path": str(payload), "name": collision.name}])
            assert offered.get("ok"), offered
            request = incoming(receiver, before)
            receiver.call({"op": "accept", "id": request["id"]})
            sent = terminal(sender, offered["id"])
            assert sent["state"] == "completed", sent
            assert collision.read_bytes() == marker
            matches = [p for p in receiver.downloads.iterdir()
                       if p.is_file() and p.name != payload.name and p.stat().st_size == payload.stat().st_size]
            assert any(sha256(p) == sha256(payload) for p in matches), matches
            passed("duplicate destination name preserved without losing received content")

            before = {t["id"] for t in receiver.transfers()}
            offered = send(sender, receiver, [{"path": str(payload), "name": "rejected.bin"}])
            assert offered.get("ok"), offered
            request = incoming(receiver, before)
            receiver.call({"op": "reject", "id": request["id"]})
            sent = terminal(sender, offered["id"])
            assert sent["state"] != "completed", sent
            assert not (receiver.downloads / "rejected.bin").exists()
            passed("rejection prevents content writes and sender success")

            # No trust bypass: a valid claimed fingerprint must still match the code.
            before = {t["id"] for t in receiver.transfers()}
            wrong_code = "0" * 16
            if receiver.info["verificationCode"].replace("-", "").lower() == wrong_code:
                wrong_code = "1" * 16
            offered = send(sender, receiver, files, code=wrong_code)
            if offered.get("ok"):
                assert terminal(sender, offered["id"])["state"] == "cancelled"
            assert {t["id"] for t in receiver.transfers()} == before
            assert not offered.get("ok"), offered
            passed("incorrect verification code refused before receive request")

            # Match the code to the claimed pin: the rejection must come from TLS,
            # not from the earlier local code-versus-fingerprint comparison.
            offered = send(sender, receiver, files, fingerprint="0" * 64, code="0" * 16)
            assert offered.get("ok"), offered
            sent = terminal(sender, offered["id"])
            assert sent["state"] == "failed" and "fingerprint mismatch" in sent.get("error", ""), sent
            assert {t["id"] for t in receiver.transfers()} == before
            passed("incorrect pinned certificate refused by actual TLS handshake")

            for name in ("../outside.bin", "C:\\outside.bin", "CON.txt", "LPT³.txt", "bad:stream", ".hidden", "trailing."):
                offered = send(sender, receiver, [{"path": str(payload), "name": name}])
                assert not offered.get("ok"), (name, offered)
            assert {t["id"] for t in receiver.transfers()} == before
            passed("unsafe paths, reserved Windows names, and alternate streams refused")

            # Outbound direction must work from either process.
            before = {t["id"] for t in sender.transfers()}
            offered = send(receiver, sender, [{"path": str(receiver.downloads / payload.name), "name": "return.bin"}])
            assert offered.get("ok"), offered
            request = incoming(sender, before)
            sender.call({"op": "accept", "id": request["id"]})
            sent = terminal(receiver, offered["id"])
            assert sent["state"] == "completed", sent
            assert sha256(sender.downloads / "return.bin") == sha256(payload)
            passed("reverse-direction transfer")

            # Cut an actual TLS stream, then measure the second attempt's wire bytes.
            # A successful retry that retransmits everything is not a resume pass.
            resume_files = [{"path": str(payload), "name": "resume.bin"}]
            with TransferProxy(receiver.info["port"], drop_after=5 * 512 * 1024) as proxy:
                before = {t["id"] for t in receiver.transfers()}
                offered = send(sender, receiver, resume_files, connect_port=proxy.port)
                assert offered.get("ok"), offered
                request = incoming(receiver, before)
                receiver.call({"op": "accept", "id": request["id"]})
                sent = terminal(sender, offered["id"])
                received = terminal(receiver, request["id"])
                assert sent["state"] != "completed" and received["state"] != "completed", (sent, received)
                assert not (receiver.downloads / "resume.bin").exists()
            partials = list((receiver.downloads / ".sharelink-partials").glob("*.part"))
            assert len(partials) == 1 and partials[0].stat().st_size >= 1024 * 1024, partials
            saved_partial_bytes = partials[0].stat().st_size
            identity = receiver.info["fingerprint"]
            receiver.stop()
            receiver.start()
            assert receiver.info["fingerprint"] == identity
            with TransferProxy(receiver.info["port"]) as proxy:
                before = {t["id"] for t in receiver.transfers()}
                offered = send(sender, receiver, resume_files, connect_port=proxy.port)
                assert offered.get("ok"), offered
                request = incoming(receiver, before)
                receiver.call({"op": "accept", "id": request["id"]})
                sent = terminal(sender, offered["id"])
                received = terminal(receiver, request["id"])
                assert sent["state"] == received["state"] == "completed", (sent, received)
                assert sha256(receiver.downloads / "resume.bin") == sha256(payload)
                assert proxy.upload_bytes < payload.stat().st_size - 512 * 1024, proxy.upload_bytes
                resumed_wire_bytes = proxy.upload_bytes
            passed("interrupted TLS stream resumes after receiver process restart",
                   sourceBytes=payload.stat().st_size, persistedBytes=saved_partial_bytes,
                   retryUploadWireBytes=resumed_wire_bytes)

            # A damaged on-disk prefix must trigger a full resend, not trust length.
            corrupt_files = [{"path": str(payload), "name": "corrupt-prefix.bin"}]
            with TransferProxy(receiver.info["port"], drop_after=5 * 512 * 1024) as proxy:
                before = {t["id"] for t in receiver.transfers()}
                offered = send(sender, receiver, corrupt_files, connect_port=proxy.port)
                assert offered.get("ok"), offered
                request = incoming(receiver, before)
                receiver.call({"op": "accept", "id": request["id"]})
                assert terminal(sender, offered["id"])["state"] == "failed"
                assert terminal(receiver, request["id"])["state"] == "failed"
            partials = list((receiver.downloads / ".sharelink-partials").glob("*.part"))
            assert len(partials) == 1 and partials[0].stat().st_size > 0, partials
            with partials[0].open("r+b") as stream:
                original = stream.read(1)
                stream.seek(0)
                stream.write(bytes([original[0] ^ 255]))
                stream.flush()
                os.fsync(stream.fileno())
            with TransferProxy(receiver.info["port"]) as proxy:
                before = {t["id"] for t in receiver.transfers()}
                offered = send(sender, receiver, corrupt_files, connect_port=proxy.port)
                assert offered.get("ok"), offered
                request = incoming(receiver, before)
                receiver.call({"op": "accept", "id": request["id"]})
                assert terminal(sender, offered["id"])["state"] == "completed"
                assert terminal(receiver, request["id"])["state"] == "completed"
                assert sha256(receiver.downloads / "corrupt-prefix.bin") == sha256(payload)
                assert proxy.upload_bytes >= payload.stat().st_size, proxy.upload_bytes
                resent_wire_bytes = proxy.upload_bytes
            passed("corrupted saved prefix triggers complete retransmission", retryUploadWireBytes=resent_wire_bytes)

            # The offer has already committed to a digest; changing the source
            # now must fail receiver verification and leave no final file.
            mutable = source / "mutated.bin"
            mutable.write_bytes(os.urandom(2 * 1024 * 1024 + 11))
            before = {t["id"] for t in receiver.transfers()}
            offered = send(sender, receiver, [{"path": str(mutable), "name": mutable.name}])
            assert offered.get("ok"), offered
            request = incoming(receiver, before)
            with mutable.open("r+b") as stream:
                byte = stream.read(1)
                stream.seek(0)
                stream.write(bytes([byte[0] ^ 255]))
                stream.flush()
                os.fsync(stream.fileno())
            receiver.call({"op": "accept", "id": request["id"]})
            sent = terminal(sender, offered["id"])
            received = terminal(receiver, request["id"])
            assert sent["state"] == received["state"] == "failed", (sent, received)
            assert "integrity check failed" in received.get("error", ""), received
            assert not (receiver.downloads / mutable.name).exists()
            assert not list((receiver.downloads / ".sharelink-partials").glob("*.part"))
            passed("source mutation fails final integrity check without committing corrupted content")

            # Cancelling a pending transfer must settle both peers without acceptance.
            before = {t["id"] for t in receiver.transfers()}
            offered = send(sender, receiver, [{"path": str(payload), "name": "cancelled.bin"}])
            assert offered.get("ok"), offered
            request = incoming(receiver, before)
            sender.call({"op": "cancel", "id": offered["id"]})
            sent = terminal(sender, offered["id"])
            assert sent["state"] == "cancelled", sent
            assert terminal(receiver, request["id"], timeout=35)["state"] == "cancelled"
            assert not (receiver.downloads / "cancelled.bin").exists()
            passed("pending cancellation settles both peers")

            with TransferProxy(receiver.info["port"], upload_delay=0.005) as proxy:
                before = {t["id"] for t in receiver.transfers()}
                offered = send(sender, receiver, [{"path": str(payload), "name": "active-cancel.bin"}], connect_port=proxy.port)
                assert offered.get("ok"), offered
                request = incoming(receiver, before)
                receiver.call({"op": "accept", "id": request["id"]})
                wait_for(lambda: find_transfer(receiver, request["id"])["bytes"] >= 1024 * 1024,
                         "received bytes before in-flight cancellation")
                receiver.call({"op": "cancel", "id": request["id"]})
                assert terminal(receiver, request["id"])["state"] == "cancelled"
                assert terminal(sender, offered["id"])["state"] == "cancelled"
                assert not (receiver.downloads / "active-cancel.bin").exists()
            passed("receiver cancellation during active streaming prevents commit and sender success")

            # Pending requests also consume the bounded session budget.
            pending = []
            peer = {"host": "127.0.0.1", "port": receiver.info["port"],
                    "fingerprint": receiver.info["fingerprint"], "name": receiver.name}
            bounded_request = {"op": "send", "peer": peer,
                               "verificationCode": receiver.info["verificationCode"],
                               "files": [{"path": str(empty), "name": "bounded.txt"}]}
            for _ in range(8):
                before = {t["id"] for t in receiver.transfers()}
                offered = sender.call(bounded_request)
                request = incoming(receiver, before)
                pending.append((offered["id"], request["id"]))
            excess = sender.call(bounded_request, require_ok=False)
            assert not excess.get("ok") and "Too many active transfers" in excess.get("error", ""), excess
            cancellation_results = {}
            for outgoing_id, _ in pending:
                cancellation_results[outgoing_id] = sender.call({"op": "cancel", "id": outgoing_id})
            for outgoing_id, incoming_id in pending:
                assert terminal(sender, outgoing_id)["state"] == "cancelled"
                settled = terminal(receiver, incoming_id)
                assert settled["state"] == "cancelled", (settled, cancellation_results[outgoing_id], sender.errors, receiver.errors)
            passed("eight active sessions are bounded and cancellation releases every pending request")

            identity = receiver.info["fingerprint"]
            receiver.stop()
            receiver.start()
            assert receiver.info["fingerprint"] == identity
            assert sha256(receiver.downloads / payload.name) == sha256(payload)
            passed("receiver identity and committed files survive process restart")

            if throughput_mib:
                bulk = source / "throughput.bin"
                generation_started = time.monotonic()
                with bulk.open("wb") as stream:
                    for _ in range(throughput_mib):
                        stream.write(os.urandom(1024 * 1024))
                    stream.flush()
                    os.fsync(stream.fileno())
                generation_seconds = time.monotonic() - generation_started
                before = {t["id"] for t in receiver.transfers()}
                preparation_started = time.monotonic()
                offered = send(sender, receiver, [{"path": str(bulk), "name": bulk.name}])
                assert offered.get("ok"), offered
                request = incoming(receiver, before)
                preparation_seconds = time.monotonic() - preparation_started
                transfer_started = time.monotonic()
                receiver.call({"op": "accept", "id": request["id"]})
                sent = terminal(sender, offered["id"], timeout=120)
                received = terminal(receiver, request["id"], timeout=120)
                transfer_seconds = time.monotonic() - transfer_started
                assert sent["state"] == received["state"] == "completed", (sent, received)
                assert sha256(bulk) == sha256(receiver.downloads / bulk.name)
                mib_per_second = throughput_mib / transfer_seconds
                passed("{} MiB loopback transfer with durable acknowledgement and exact digest".format(throughput_mib),
                       sizeBytes=bulk.stat().st_size, fileGenerationSeconds=round(generation_seconds, 4),
                       sourceHashConnectOfferSeconds=round(preparation_seconds, 4),
                       acceptToDurableCompletionSeconds=round(transfer_seconds, 4),
                       mebibytesPerSecond=round(mib_per_second, 2),
                       megabitsPerSecond=round(mib_per_second * 1024 * 1024 * 8 / 1000000, 2),
                       scope="single-host loopback only; includes receive hashing, fsync and polling; not Wi-Fi performance")
                print("MEASUREMENT: {:.2f} MiB/s, {:.3f} s transfer, {:.3f} s source hash/connect/offer".format(
                    mib_per_second, transfer_seconds, preparation_seconds), flush=True)
            report["status"] = "passed"
        except Exception as error:
            report["status"] = "failed"
            report["error"] = repr(error)
            raise
        finally:
            sender.stop()
            if receiver:
                receiver.stop()
            report["finishedAt"] = datetime.now().astimezone().isoformat()
            save_report()
    print("All CLI integration checks passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("--throughput-mib", type=int, default=128,
                        help="loopback payload size in MiB; 0 skips the measurement (default: 128)")
    parser.add_argument("--report", type=Path, help="write actual check results and measured throughput as JSON")
    args = parser.parse_args()
    executable_path = args.executable.resolve()
    if not executable_path.is_file():
        parser.error(f"CLI executable does not exist: {executable_path}")
    if not 0 <= args.throughput_mib <= 4096:
        parser.error("--throughput-mib must be between 0 and 4096")
    run(executable_path, args.throughput_mib, args.report)
