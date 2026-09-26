# Runtime-free fallback outbox

If the native Praxis executable cannot run, write one JSON envelope per transaction here using `protocol/praxis-envelope-v1.schema.json`.

The envelope is a proposal. CI independently validates the actual Git ref and commit, agent identity, ordering, evidence and requested state transitions before anything becomes canonical. Never edit canonical `.ros` state by hand as a substitute.
