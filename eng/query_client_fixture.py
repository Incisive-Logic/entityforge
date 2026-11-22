"""Owned local server lifecycle for optional independent HTTP client checks."""

import argparse
import hashlib
import json
import os
import platform
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
from urllib.request import Request, urlopen


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def artifact_hashes(server):
    paths = sorted(path for path in server.parent.iterdir() if path.is_file() and
                   (path.suffix == ".dll" or path.name.endswith((".deps.json", ".runtimeconfig.json"))))
    result = {}
    for path in paths:
        with path.open("rb") as stream:
            result[path.name] = hashlib.file_digest(stream, "sha256").hexdigest()
    require(server.name in result, "Server must be a managed DLL alongside its dependencies")
    return result


def run_client_checks(description, profile, client_name, client_version, exercise_all, sort_bytes=0):
    parser = argparse.ArgumentParser(description=description)
    parser.add_argument("--server", required=True, type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    server = args.server.resolve(strict=True)
    socket.setdefaulttimeout(15)
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    base = "http://127.0.0.1:" + str(port)
    tokens = {role: secrets.token_hex(32) for role in ("ADMIN", "WRITER", "READER")}
    # Remove inherited server settings so the fixture cannot use a real data path,
    # token file, external listener or deployment-specific configuration.
    env = {k: v for k, v in os.environ.items()
           if not k.startswith(("ENTITYFORGE_", "ASPNETCORE_", "DOTNET_Dbg", "COMPlus_Dbg"))}
    env.update({"ENTITYFORGE_DB_PROFILE": profile, "ENTITYFORGE_DB_REQUIRE_HTTPS": "false",
                "ASPNETCORE_URLS": base, "DOTNET_DbgEnableMiniDump": "0",
                "Logging__LogLevel__Default": "Warning"})
    env.update({"ENTITYFORGE_DB_" + role + "_TOKEN": token for role, token in tokens.items()})
    env["ENTITYFORGE_DB_SCAN_SORT_TEMP_BYTES"] = str(sort_bytes)
    results = []
    inputs = artifact_hashes(server)
    report = {"client": client_name, "version": client_version, "profile": profile, "cases": results,
              "scanSortTemporaryBytes": sort_bytes, "status": "running", "platform": platform.platform(),
              "serverSha256": inputs[server.name], "artifactsBefore": inputs,
              "ownedFixtureRemoved": False, "ownedServerStopped": False}
    # Replace an earlier success before launching: an interrupted rerun must not
    # leave stale passing evidence at the requested report path.
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    fixture = None
    process = None
    try:
        with tempfile.TemporaryDirectory(prefix="entityforge-client-") as fixture:
            env["ENTITYFORGE_DB_DATA_DIRECTORY"] = str(Path(fixture) / "data")
            with open(Path(fixture) / "server.log", "w+b") as log:
                process = subprocess.Popen([args.dotnet, str(server)], cwd=fixture, env=env, stdout=log, stderr=log)
                try:
                    deadline = time.monotonic() + 45
                    while True:
                        require(process.poll() is None, "Owned server exited during startup")
                        try:
                            request = Request(base + "/v1/" + ("rdf/sparql" if profile == "rdf" else "graph/gql/query"),
                                data=json.dumps({"query": "ASK {}" if profile == "rdf" else "RETURN 1 AS ready"}).encode(),
                                headers={"Authorization": "Bearer " + tokens["READER"], "Content-Type": "application/json"})
                            with urlopen(request, timeout=2) as response:
                                require(response.status == 200, "Startup query failed")
                            break
                        except OSError:
                            if time.monotonic() >= deadline:
                                raise TimeoutError("Owned server did not become ready")
                            time.sleep(0.1)
                    results = exercise_all(base, tokens["READER"], tokens["WRITER"])
                finally:
                    process.terminate()
                    try:
                        process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=15)
        report["artifactsAfter"] = artifact_hashes(server)
        report["artifactsUnchanged"] = inputs == report["artifactsAfter"]
        require(report["artifactsUnchanged"], "Server artifacts changed during client qualification")
        report["status"] = "passed"
        report["cases"] = results
    except BaseException as error:
        report["status"] = "failed"
        # Exception messages may contain credentials or response data. Keep the
        # exception class only; the invoking console retains the traceback.
        report["failureType"] = type(error).__name__
        raise
    finally:
        report["ownedFixtureRemoved"] = fixture is None or not Path(fixture).exists()
        report["ownedServerStopped"] = process is None or process.poll() is not None
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("%s: %d cases passed; owned server stopped and fixture removed." % (client_name, len(results)))
