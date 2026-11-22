"""Exercise RDFLib against an owned, temporary EntityForge RDF server.

Requires Python 3.11+ and RDFLib 7.6.0. Never connects to an existing database.
Pass --server pointing to EntityForge.Db.Server.dll and --dotnet to its runtime.
"""

import secrets
import rdflib
from rdflib import BNode, Literal, URIRef
from rdflib.plugins.stores.sparqlconnector import SPARQLConnector
from query_client_fixture import require, run_client_checks


def exercise(base, reader, writer, method, result_format):
    query = SPARQLConnector(
        query_endpoint=base + "/sparql", method=method,
        returnFormat=result_format, headers={"Authorization": "Bearer " + reader})
    update = SPARQLConnector(
        update_endpoint=base + "/sparql/update",
        headers={"Authorization": "Bearer " + writer})
    graph = "urn:rdflib:" + secrets.token_hex(8)
    update.update("""INSERT DATA { GRAPH <%s> {
        <urn:client:s> <urn:client:label> 'bonjour 雪'@fr ;
          <urn:client:number> '007'^^<http://www.w3.org/2001/XMLSchema#integer> .
        _:b <urn:client:label> 'blank' .
    } }""" % graph)
    pattern = "?s <urn:client:label> ?label ; <urn:client:number> ?number"
    projection = "SELECT ?s ?label ?number ?missing WHERE { "
    for dataset in (False, True):
        text = projection + (pattern if dataset else "GRAPH <%s> { %s }" % (graph, pattern)) + " }"
        result = query.query(text, default_graph=graph if dataset else None)
        require([str(v) for v in result.vars] == ["s", "label", "number", "missing"], "Projection header")
        rows = list(result)
        require(len(rows) == 1, "Expected one solution")
        row = rows[0]
        require(row.s == URIRef("urn:client:s"), "IRI term")
        require(row.label == Literal("bonjour 雪", lang="fr"), "Language term")
        # RDFLib normally canonicalizes numeric lexical forms on construction.
        # Compare the value/datatype here; raw lexical fidelity has separate wire tests.
        require(int(row.number) == 7 and row.number.datatype == rdflib.XSD.integer, "Typed numeric term")
        require(row.missing is None, "Unbound variable")
    require(bool(query.query("ASK { GRAPH <%s> { ?s ?p ?o } }" % graph)), "ASK true")
    blanks = list(query.query("SELECT ?b (?b AS ?same) WHERE { GRAPH <%s> { ?b <urn:client:label> 'blank' } }" % graph))
    require(len(blanks) == 1 and isinstance(blanks[0].b, BNode) and blanks[0].b == blanks[0].same, "Blank-node identity")
    constructed = SPARQLConnector(
        query_endpoint=base + "/sparql", method=method, returnFormat="turtle",
        headers={"Authorization": "Bearer " + reader}).query(
            "CONSTRUCT { ?s <urn:client:label> ?label } WHERE { GRAPH <%s> { %s } }" % (graph, pattern))
    require(set(constructed.graph) == {(URIRef("urn:client:s"), URIRef("urn:client:label"), Literal("bonjour 雪", lang="fr"))}, "CONSTRUCT graph")
    update.update("DELETE WHERE { GRAPH <%s> { ?s ?p ?o } }" % graph)
    require(not bool(query.query("ASK { GRAPH <%s> { ?s ?p ?o } }" % graph)), "ASK false after delete")
    return {"method": method, "format": result_format, "passed": True}


def exercise_all(base, reader, writer):
    require(rdflib.__version__ == "7.6.0", "Install the qualified RDFLib 7.6.0 version")
    return [exercise(base, reader, writer, method, result_format)
            for method in ("GET", "POST", "POST_FORM") for result_format in ("json", "xml")]


if __name__ == "__main__":
    run_client_checks(__doc__, "rdf", "RDFLib", rdflib.__version__, exercise_all)
