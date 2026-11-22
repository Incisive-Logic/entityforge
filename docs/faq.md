# Frequently asked questions

## Has AI been used to build EntityForge?

Yes. AI tools have assisted with implementation, tests, documentation and analysis
during EntityForge's development. Incisive Logic remains responsible for the
platform and its published claims.

AI assistance is not evidence of correctness, standards compliance or production
readiness. See the [query support documentation](user/query-languages.md) for
supported features and limitations, and the
[qualification documentation](developer/query-compatibility.md) for how behavior
is tested and what that evidence establishes.

## How should I interpret performance results?

Performance depends on the workload, hardware, storage, configuration and
concurrent activity. A result on one machine is not a guarantee for another
deployment. The [public benchmark runner](developer/benchmark-runner.md) covers
Graph, RDF and Semantic workloads, including native queries and mutations,
mapped entity operations, GraphQL and selected lifecycle operations. Embedded,
HTTP and opt-in distributed fixtures have different coverage; consult the
workload matrix rather than assuming every operation runs in every mode.
Semantic native queries measure bounded asserted views, not inference. Local
distributed fixtures do not establish cross-machine scaling.

The [benchmark methodology](developer/query-benchmarks.md) explains how to run
these measurements and what must accompany published results: reproducible
workloads, build identity, machine and configuration details, correctness checks,
raw results, repetitions and failed attempts. AI assistance in developing or
analysing a benchmark should also be disclosed. Performance claims must be based
on recorded executions.

## What does “for people & AI” mean?

People can explore connected data in Studio; applications can retrieve typed
facts, relationships and source evidence to include in an AI model's context.
The Semantic profile helps express a consistent knowledge model across source
data while retaining assertion provenance. Retrieving updated facts does not
require retraining the model. EntityForge is not itself an LLM, and structured
context does not guarantee a correct answer: retrieval, permissions and the
application's use of the evidence still matter.

## Is EntityForge free for commercial use?

Yes. Author-owned code is Apache-2.0, with no licence fee for personal or
commercial use. The grant for a released version is perpetual, subject to the
licence terms. Third-party components retain their licences. This does not
promise free hosting, support or future services; see [licensing](../LICENSING.md).
