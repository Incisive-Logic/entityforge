"""Check EntityForge's GQL HTTP contract with Python's standard HTTP/JSON client.

Requires Python 3.11+. Launches an owned temporary property-graph server.
This tests EntityForge's wire contract, not an ISO-standard GQL wire protocol.
"""

import json
from decimal import Decimal
import platform
from urllib.error import HTTPError
from urllib.request import Request, urlopen
import uuid

from query_client_fixture import require, run_client_checks


def exercise_all(base, reader, writer):
    checks = []

    def post(route, body, token=reader, expected=200):
        headers = {"Content-Type": "application/json", "Accept": "application/json"}
        if token is not None:
            headers["Authorization"] = "Bearer " + token
        try:
            response = urlopen(Request(base + route, data=json.dumps(body).encode(), headers=headers))
        except HTTPError as error:
            response = error
        with response:
            raw = response.read(4 * 1024 * 1024 + 1)
            require(len(raw) <= 4 * 1024 * 1024, "Response exceeded client bound")
            require(response.status == expected, "%s: expected HTTP %d, received %d: %s" % (route, expected, response.status, raw[:512]))
            return json.loads(raw, parse_float=Decimal)

    def passed(name):
        checks.append({"name": name, "passed": True})

    query_route = "/v1/graph/gql/query"
    update_route = "/v1/graph/gql/update"
    post(query_route, {"query": "RETURN 1 AS answer"}, token=None, expected=401)
    passed("unauthenticated query rejected")
    statement = "INSERT (:ClientPerson {name: $name, age: $age})"
    post(update_route, {"transactionId": str(uuid.uuid4()), "statement": statement,
                        "parameters": {"name": "denied", "age": 1}}, expected=401)
    passed("reader update rejected")
    for name, age in (("Zoë 雪", 42), ("Alice", 21)):
        post(update_route, {"transactionId": str(uuid.uuid4()), "statement": statement,
                            "parameters": {"name": name, "age": age}}, token=writer)
    passed("authenticated parameterized inserts")
    query = "MATCH (p:ClientPerson) WHERE p.age >= $minimum RETURN p.name AS name, p.age AS age, p.missing AS absent ORDER BY name"
    body = {"query": query, "parameters": {"minimum": 18}}
    result = post(query_route, body)
    rows = [row["bindings"] for row in result["matches"]]
    require(rows == [{"name": "Alice", "age": 21, "absent": None}, {"name": "Zoë 雪", "age": 42, "absent": None}], "Ordered Unicode/numeric/null bindings")
    require(isinstance(result["snapshotVersion"], int), "Snapshot version")
    passed("parameterized ordered scalar results")
    pinned = post(query_route, dict(body, snapshotVersion=result["snapshotVersion"]))
    require(pinned["matches"] == result["matches"], "Available snapshot changed")
    post(update_route, {"transactionId": str(uuid.uuid4()), "statement": "MATCH (p:ClientPerson) SET p.age = p.age + 1"}, token=writer)
    expired = post(query_route, dict(body, snapshotVersion=result["snapshotVersion"]), expected=410)
    require(expired["code"] == "graph_snapshot_unavailable", "Expired snapshot diagnostic")
    latest = post(query_route, body)
    require([row["bindings"]["age"] for row in latest["matches"]] == [22, 43], "Latest update values")
    passed("available snapshot, default retention expiry and latest visibility")
    empty = post(query_route, {"query": query, "parameters": {"minimum": 100}})
    require(empty["matches"] == [], "Empty result")
    passed("successful empty result")
    typed = post(query_route, {"query": "RETURN CAST(RECORD {amount:9007199254740993, "
        "items:[1,2], label:'Zoë 雪'} AS RECORD {amount DEC, items LIST<DEC>[2], label STRING}) AS recordValue, "
        "0.1234567890123456789012345678M AS precise, "
        "CAST(NULL AS LIST<INTEGER>) AS absent"})
    require([row["bindings"] for row in typed["matches"]] == [{
        "recordValue": {"amount": Decimal("9007199254740993"), "items": [Decimal(1), Decimal(2)], "label": "Zoë 雪"},
        "precise": Decimal("0.1234567890123456789012345678"), "absent": None}], "Structured cast or exact decimal wire value")
    passed("nested record/list casts and exact decimal JSON decoding")
    for expression, status in (
        ("CAST(RECORD {items:[1,2]} AS RECORD {items LIST<INTEGER>[1]})", "2202F"),
        ("CAST(RECORD {amount:1.5M} AS RECORD {amount INTEGER})", "22003"),
    ):
        error = post(query_route, {"query": "RETURN " + expression + " AS rejected"}, expected=400)
        require(error.get("gqlStatus") == status, "Query cast diagnostic")
        rejected = {"transactionId": str(uuid.uuid4()), "statement":
            "INSERT (:ClientCastFailure) RETURN " + expression + " AS rejected"}
        for retry in range(2):
            error = post(update_route, rejected, token=writer, expected=400)
            require(error.get("gqlStatus") == status, "Update cast diagnostic on retry %d: %s" % (retry, error))
        unchanged = post(query_route, {"query": "MATCH (n:ClientCastFailure) RETURN n"})
        require(unchanged["matches"] == [], "Failed cast committed an inserted node")
        require(unchanged["snapshotVersion"] == typed["snapshotVersion"], "Failed cast advanced the graph version")
        passed("structured cast %s query/update diagnostics and atomic retry" % status)
    post(query_route, {"query": "MATCH (p:ClientPerson) DELETE p FINISH"}, expected=400)
    require(len(post(query_route, body)["matches"]) == 2, "Rejected query changed graph")
    passed("query endpoint rejects mutation without changes")
    request = Request(base + query_route + "/scan", data=json.dumps(body).encode(), headers={
        "Authorization": "Bearer " + reader, "Content-Type": "application/json", "Accept": "application/x-ndjson"})
    frames = []
    with urlopen(request) as response:
        while True:
            line = response.readline(1024 * 1024 + 1)
            if not line:
                break
            require(len(line) <= 1024 * 1024 and len(frames) < 16, "Scan client bound")
            frames.append(json.loads(line))
    require(frames and frames[-1]["type"] == "complete" and frames[-1]["complete"] is True, "Missing scan completion")
    require(all(frame["type"] == "page" for frame in frames[:-1]), "Unexpected scan frame sequence")
    scan_rows = [row["bindings"] for frame in frames[:-1] for row in frame["rows"]]
    require(scan_rows == [row["bindings"] for row in latest["matches"]], "Scan row mismatch")
    passed("NDJSON ordered rows with explicit completion")
    return checks


if __name__ == "__main__":
    run_client_checks(__doc__, "property-graph", "Python urllib/json", platform.python_version(), exercise_all,
                      sort_bytes=8 * 1024 * 1024)
