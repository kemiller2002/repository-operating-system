# Dual-entry execution, reconciliation, and input inbox

Status: approved
Work item: WI-0064
Branch invariant: WI-0064

## Objective
Praxis MUST remain usable when an agent cannot execute the native Praxis runtime. Native execution and a runtime-free JSON envelope are two entry paths into the same canonical work/event model.

## Requirements
1. Every work item MUST execute on a distinct branch whose name is the work-item ID. A claimed work-item/branch mismatch MUST fail reconciliation.
2. Agents MUST attempt the native Praxis executable first. If unavailable or unusable, they MAY submit a JSON envelope without installing a local runtime.
3. The envelope is untrusted proposed input, never canonical state. It MUST contain schema version, transaction ID, work-item ID, claimed branch, base commit, structured agent identity, ordered timeline, requested transitions/events, evidence and artifact references.
4. CI MUST independently observe the actual ref/commit and reconcile the envelope through the same domain rules used by native Praxis. JSON MUST NOT bypass transition, evidence, provenance, identity, attribution, or validation rules.
5. Reconciliation MUST be deterministic and idempotent. Replaying a transaction after partial failure MUST not duplicate events, telemetry, provenance, or transitions.
6. Accepted envelope contents MUST become durable canonical Praxis history before transient input is removed. Rejected input MUST remain available with machine-readable diagnostics and MUST NOT partially mutate canonical state.
7. Successful reconciliation MUST commit canonical state, create a checkpoint tag tied to the transaction, and remove the transient envelope. Work-item branch deletion is separate and occurs only under the normal completion/merge lifecycle.
8. Praxis MUST provide an input-documents inbox for unprocessed human/agent material. Inputs may include requirements, research, decisions, notes and other supported documents.
9. Inbox classification is advisory, not trusted. Processing MUST discover requirements, decisions, constraints, evidence, risks, open questions and references, preserving provenance to the original input.
10. An input MUST remain unprocessed until every derived canonical mutation is durably reconciled. Claiming/processing MUST be crash-safe and idempotent.
11. An agent entering a repository MUST be able to discover pending inputs and fallback-envelope instructions without a Praxis runtime.
12. Agent identity is mandatory for native and envelope paths and MUST propagate to derived requirements, events, timeline entries, provenance and telemetry.
13. Cross-work-item contamination MUST be rejected unless an explicit multi-work-item protocol is introduced later.
14. The protocol MUST be sufficient to bootstrap participation in a repository where Praxis cannot yet execute locally.
15. CI is the authoritative reconciliation boundary.
16. Every Praxis installation MUST have a stable Praxis instance identity that is distinct from repository identity, agent identity, agent-session identity, work-item identity and transaction identity.
17. Praxis MUST generate and persist its instance identity locally before any external registration is attempted. The local instance record is authoritative for that identity.
18. A Praxis instance MUST remain fully operational when Echelon Registry, Vigila, the network, credentials or any other Echelon component is unavailable. Registration MUST NOT be an installation, execution or reconciliation dependency.
19. When Echelon Registry is available and authorized, Praxis SHOULD register or refresh a discoverable projection of the instance. Registration MUST be retryable and idempotent.
20. Instance registration SHOULD advertise repository/provider coordinates, Praxis version, reconciliation protocol version, supported capabilities and the known availability state of optional Echelon integrations. Unknown capability/integration state MUST be representable without being treated as failure.
21. Registry data is a discovery/heartbeat projection and MUST NOT replace the locally authoritative instance identity or local canonical Praxis state.
22. Native execution and fallback envelopes MUST carry the Praxis instance identity so canonical provenance can establish instance -> repository -> work item -> branch -> agent -> agent session -> transaction -> event/evidence/artifact.
23. Reconciliation MUST reject a claimed instance identity that conflicts with the locally authoritative Praxis instance identity, except through an explicit and auditable instance migration/recovery operation.
24. Instance identity MUST survive Praxis upgrades and normal repository lifecycle operations. Cloning, templating, repository transfer and intentional reinstallation MUST have explicit rules preventing accidental identity duplication.
25. Instance registration MUST minimize disclosed data and MUST NOT publish credentials, secrets, private artifact contents or agent-private runtime data.
26. The instance model MUST support capability/version discovery without requiring all Echelon systems to be installed, preserving independently installable components.

## Required repository layout
```
.praxis/
  inbox/
    documents/
  outbox/
    events/
  processing/
  rejected/
```

The physical layout MAY evolve only if the same lifecycle and discoverability guarantees remain.

## Acceptance criteria
- Native and envelope paths produce semantically equivalent canonical outcomes for the same legal work.
- A fresh Praxis installation creates a stable local instance identity before attempting Registry access.
- Praxis continues to install, execute and reconcile when Registry is absent or unreachable.
- Registration retry is idempotent and does not create duplicate instance identities.
- Reconciliation rejects a spoofed/mismatched Praxis instance identity.
- Upgrade tests prove instance identity is preserved.
- Clone/template tests prove an instance identity is not accidentally duplicated into a logically new installation.
- Registry projection tests prove capability/version/integration states can be advertised independently and can represent unknown/unavailable states.
- Registration output contains no credentials or secret material.
- Invalid schema, missing identity, branch mismatch, stale/invalid base, illegal transition, invalid evidence and duplicate transaction tests exist.
- Crash/retry tests prove idempotency before and after canonical commit.
- Inbox claim/retry/reconcile tests prove no source is lost.
- Validation rejects meaningful work on a branch other than its work-item ID.
- Successful reconciliation leaves no transient accepted envelope and leaves durable provenance plus a checkpoint.
- Documentation includes a runtime-free agent bootstrap example.
