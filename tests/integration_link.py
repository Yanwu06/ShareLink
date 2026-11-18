"""Verify link selection, authenticated control, and reverse transfer in two processes.

No wireless hardware is simulated here: this checks the real shared core on TCP loopback.
"""
from __future__ import annotations

import argparse
from copy import deepcopy
from datetime import datetime
import json
from pathlib import Path
import tempfile
import time

from integration_cli import Node, incoming, sha256, terminal, wait_for


def run(executable: Path, report_path: Path):
    report = {
        "startedAt": datetime.now().astimezone().isoformat(),
        "executableSha256": sha256(executable),
        "scope": "two independent cores; loopback; no BLE or Wi-Fi hardware validation",
        "checks": [], "status": "running",
    }

    def record(name):
        report["checks"].append({"name": name, "status": "passed"})
        print("PASS", name, flush=True)

    with tempfile.TemporaryDirectory(prefix="sharelink-link-") as temporary:
        root = Path(temporary)
        a = Node(executable, root / "windows", "Windows test")
        b = Node(executable, root / "android", "Android test")
        try:
            link = {"standard": "Wi-Fi 5", "band": "5 GHz", "source": "test-fixture", "txMbps": None}
            b.call({"op": "report_link", "report": link})
            assert b.call({"op": "snapshot"})["info"]["linkReport"] == link
            peer = a.call({"op": "probe", "host": "127.0.0.1", "port": b.info["port"]})["peer"]
            assert peer["fingerprint"] == b.info["fingerprint"]
            record("link report retains unknown values and TLS identity")

            base = {"op": "select_path", "currentBytesPerSecond": 40_000_000,
                    "candidateBytesPerSecond": 90_000_000, "setupSeconds": 8,
                    "keepCurrentNetwork": False}
            assert a.call(dict(base, remainingBytes=20_000_000))["choice"] == "current"
            large = a.call(dict(base, remainingBytes=2_000_000_000))
            assert large["choice"] == "candidate" and large["savedSeconds"] > 3
            assert a.call(dict(base, remainingBytes=2_000_000_000, keepCurrentNetwork=True))["choice"] == "current"
            assert a.call(dict(base, remainingBytes=2_000_000_000, candidateBytesPerSecond=None))["choice"] == "current"
            assert a.call(dict(base, remainingBytes=2_000_000_000, currentBytesPerSecond=None,
                               candidateBytesPerSecond=None))["choice"] == "unavailable"
            record("small/large/keep-network/unknown path decisions")

            for direction in ("upload", "download"):
                before = len(b.transfers())
                started = time.monotonic()
                result = a.call({"op": "measure", "peer": peer,
                                 "verificationCode": b.info["verificationCode"], "direction": direction})
                assert 0 < result["bytes"] <= 16 * 1024 * 1024
                assert result["bytesPerSecond"] > 0 and result["durationMs"] >= 0
                assert result["direction"] == direction and result["source"] == "tls-memory-probe"
                assert time.monotonic() - started < 5
                assert len(b.transfers()) == before
                record("bounded TLS memory measurement " + direction)
            bad = a.call({"op": "measure", "peer": peer, "verificationCode": "0000000000000000",
                          "direction": "upload"}, require_ok=False)
            assert not bad["ok"]
            record("measurement rejects wrong identity verification")

            pa = a.call({"op": "pair_begin"})
            pb = b.call({"op": "pair_begin"})
            fa = a.call({"op": "pair_finish", "pairId": pa["pairId"], "hello": pb["hello"]})
            fb = b.call({"op": "pair_finish", "pairId": pb["pairId"], "hello": pa["hello"]})
            assert fa["sas"] == fb["sas"] and len(fa["sas"]) == 6 and fa["sas"].isdigit()
            early = a.call({"op": "pair_encrypt", "pairId": pa["pairId"],
                            "payload": {"op": "hotspot_start"}}, require_ok=False)
            assert not early["ok"]
            record("matching SAS and confirmation required before encryption")

            a.call({"op": "pair_confirm", "pairId": pa["pairId"], "confirmed": True})
            b.call({"op": "pair_confirm", "pairId": pb["pairId"], "confirmed": True})
            payload = {"op": "credentials", "ssid": "ShareLink 测试", "password": "test-only-secret"}
            envelope = a.call({"op": "pair_encrypt", "pairId": pa["pairId"], "payload": payload})["envelope"]
            assert "test-only-secret" not in json.dumps(envelope)
            plain = b.call({"op": "pair_decrypt", "pairId": pb["pairId"], "envelope": envelope})["payload"]
            assert plain == payload
            replay = b.call({"op": "pair_decrypt", "pairId": pb["pairId"], "envelope": envelope}, require_ok=False)
            assert not replay["ok"]
            record("encrypted Unicode control payload and replay rejection")

            reverse = b.call({"op": "pair_encrypt", "pairId": pb["pairId"], "payload": {"op": "status"}})["envelope"]
            assert a.call({"op": "pair_decrypt", "pairId": pa["pairId"], "envelope": reverse})["payload"]["op"] == "status"
            damaged = deepcopy(a.call({"op": "pair_encrypt", "pairId": pa["pairId"], "payload": {"op": "status"}})["envelope"])
            cipher = damaged["ciphertext"]
            damaged["ciphertext"] = ("0" if cipher[0] != "0" else "1") + cipher[1:]
            assert not b.call({"op": "pair_decrypt", "pairId": pb["pairId"], "envelope": damaged}, require_ok=False)["ok"]
            a.call({"op": "pair_close", "pairId": pa["pairId"]})
            assert not a.call({"op": "pair_encrypt", "pairId": pa["pairId"], "payload": {}}, require_ok=False)["ok"]
            b.call({"op": "pair_close", "pairId": pb["pairId"]})
            record("bidirectional control, ciphertext integrity and pair closure")

            source = root / "phone-source.bin"
            source.write_bytes(bytes(range(256)) * (8 * 1024))
            files = [{"name": "手机到电脑-测试.bin", "path": str(source)}]
            offer = b.call({"op": "prepare_pull", "files": files})
            before = {item["id"] for item in a.transfers()}
            pulled = a.call({"op": "pull", "peer": peer,
                             "verificationCode": b.info["verificationCode"], "token": offer["token"]})
            incoming(a, before)
            request = wait_for(lambda: next((item for item in a.transfers()
                if item["id"] == pulled["id"] and item["state"] in ("awaiting_accept", "failed")), None),
                "reverse receive offer to reach explicit confirmation")
            assert request["state"] == "awaiting_accept"
            a.call({"op": "accept", "id": request["id"]})
            received = terminal(a, pulled["id"])
            sent = terminal(b, offer["id"])
            assert received["state"] == sent["state"] == "completed", (received, sent)
            assert sha256(Path(received["files"][0]["path"])) == sha256(source)
            record("phone-server reverse transfer with explicit Windows acceptance and durable file")

            reuse = a.call({"op": "pull", "peer": peer,
                           "verificationCode": b.info["verificationCode"], "token": offer["token"]}, require_ok=False)
            if reuse.get("ok"):
                assert terminal(a, reuse["id"])["state"] == "failed"
            record("reverse transfer token cannot be reused")

            def pull_pending():
                pending_offer = b.call({"op": "prepare_pull", "files": files})
                pending_pull = a.call({"op": "pull", "peer": peer,
                    "verificationCode": b.info["verificationCode"], "token": pending_offer["token"]})
                pending_request = wait_for(lambda: next((item for item in a.transfers()
                    if item["id"] == pending_pull["id"] and item["state"] in ("awaiting_accept", "failed")), None),
                    "reverse offer confirmation")
                assert pending_request["state"] == "awaiting_accept", pending_request
                return pending_offer, pending_pull

            saved_before = {p: sha256(p) for p in a.downloads.rglob("*") if p.is_file()}
            declined_offer, declined_pull = pull_pending()
            a.call({"op": "reject", "id": declined_pull["id"]})
            declined_received = terminal(a, declined_pull["id"])
            declined_sent = terminal(b, declined_offer["id"])
            assert declined_received["state"] == declined_sent["state"] == "rejected"
            assert "declined" in declined_received["error"].lower()
            assert "declined" in declined_sent["error"].lower()
            assert declined_received["bytes"] == declined_sent["bytes"] == 0
            assert saved_before == {p: sha256(p) for p in a.downloads.rglob("*") if p.is_file()}
            record("reverse rejection settles both endpoints without saving content")

            abandoned = b.call({"op": "prepare_pull", "files": files})
            b.call({"op": "cancel", "id": abandoned["id"]})
            assert terminal(b, abandoned["id"])["state"] == "cancelled"
            abandoned_pull = a.call({"op": "pull", "peer": peer,
                "verificationCode": b.info["verificationCode"], "token": abandoned["token"]})
            assert terminal(a, abandoned_pull["id"])["state"] == "failed"
            assert saved_before == {p: sha256(p) for p in a.downloads.rglob("*") if p.is_file()}
            record("cancelling an unconsumed phone offer prevents subsequent transfer")

            cancelled_offer, cancelled_pull = pull_pending()
            a.call({"op": "cancel", "id": cancelled_pull["id"]})
            assert terminal(a, cancelled_pull["id"])["state"] == "cancelled"
            assert terminal(b, cancelled_offer["id"])["state"] == "cancelled"
            assert saved_before == {p: sha256(p) for p in a.downloads.rglob("*") if p.is_file()}
            record("reverse cancellation while awaiting acceptance settles the phone")

            retry_offer, retry_pull = pull_pending()
            a.call({"op": "accept", "id": retry_pull["id"]})
            retried_received = terminal(a, retry_pull["id"])
            retried_sent = terminal(b, retry_offer["id"])
            assert retried_received["state"] == retried_sent["state"] == "completed"
            retried_path = Path(retried_received["files"][0]["path"])
            assert retried_path not in saved_before and sha256(retried_path) == sha256(source)
            assert all(sha256(p) == digest for p, digest in saved_before.items())
            record("fresh reverse offer succeeds after rejection/cancellation and preserves same-name files")
            report["status"] = "passed"
        except Exception as error:
            report["status"] = "failed"
            report["error"] = str(error)
            raise
        finally:
            a.stop(); b.stop()
            report["finishedAt"] = datetime.now().astimezone().isoformat()
            report_path.parent.mkdir(parents=True, exist_ok=True)
            report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"{len(report['checks'])} link/control checks passed", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("executable", type=Path)
    parser.add_argument("--report", type=Path, default=Path("artifacts/validation/link-integration.json"))
    args = parser.parse_args()
    run(args.executable.resolve(), args.report.resolve())
