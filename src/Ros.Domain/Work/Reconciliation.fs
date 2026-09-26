namespace Ros.Domain.Work

open System
open System.Text.RegularExpressions

type EnvelopeActor = {
    ActorKind: string
    ActorId: string
    Provider: string option
    Model: string option
    Runtime: string option
}

type EnvelopeTimelineEntry = {
    Sequence: int
    Timestamp: DateTimeOffset
    Action: string
}

type EnvelopeRequest = {
    RequestType: string
}

type ReconciliationEnvelope = {
    SchemaVersion: string
    TransactionId: string
    WorkItem: string
    Branch: string
    BaseCommit: string
    Agent: EnvelopeActor
    Timeline: EnvelopeTimelineEntry list
    Requests: EnvelopeRequest list
}

type ReconciliationObservation = {
    ActualBranch: string
    HeadCommit: string
    BaseCommitExists: bool
    TransactionAlreadyApplied: bool
}

type ReconciliationFinding =
    | UnsupportedSchemaVersion of string
    | MissingTransactionId
    | MissingAgentIdentity
    | InvalidWorkItemId of string
    | WorkItemBranchMismatch of workItem: string * claimedBranch: string
    | ObservedBranchMismatch of claimedBranch: string * actualBranch: string
    | InvalidBaseCommit of string
    | BaseCommitUnavailable of string
    | EmptyRequests
    | InvalidTimelineSequence
    | DuplicateTransaction of string

type ReconciliationDecision =
    | Accept
    | AlreadyApplied
    | Reject of ReconciliationFinding list

module Reconciliation =
    let private sha = Regex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)
    let private workItem = Regex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)

    let decide (envelope: ReconciliationEnvelope) (observed: ReconciliationObservation) =
        if observed.TransactionAlreadyApplied then AlreadyApplied
        else
            let findings = [
                if envelope.SchemaVersion <> "1.0" then UnsupportedSchemaVersion envelope.SchemaVersion
                if String.IsNullOrWhiteSpace envelope.TransactionId then MissingTransactionId
                if String.IsNullOrWhiteSpace envelope.Agent.ActorKind || String.IsNullOrWhiteSpace envelope.Agent.ActorId then MissingAgentIdentity
                if not (workItem.IsMatch envelope.WorkItem) then InvalidWorkItemId envelope.WorkItem
                if envelope.WorkItem <> envelope.Branch then WorkItemBranchMismatch(envelope.WorkItem, envelope.Branch)
                if envelope.Branch <> observed.ActualBranch then ObservedBranchMismatch(envelope.Branch, observed.ActualBranch)
                if not (sha.IsMatch envelope.BaseCommit) then InvalidBaseCommit envelope.BaseCommit
                elif not observed.BaseCommitExists then BaseCommitUnavailable envelope.BaseCommit
                if envelope.Requests.IsEmpty then EmptyRequests
                let expected = [1 .. envelope.Timeline.Length]
                let actual = envelope.Timeline |> List.map _.Sequence
                if actual <> expected then InvalidTimelineSequence
            ]
            if findings.IsEmpty then Accept else Reject findings

    let findingCode = function
        | UnsupportedSchemaVersion _ -> "unsupported-schema-version"
        | MissingTransactionId -> "missing-transaction-id"
        | MissingAgentIdentity -> "missing-agent-identity"
        | InvalidWorkItemId _ -> "invalid-work-item-id"
        | WorkItemBranchMismatch _ -> "work-item-branch-mismatch"
        | ObservedBranchMismatch _ -> "observed-branch-mismatch"
        | InvalidBaseCommit _ -> "invalid-base-commit"
        | BaseCommitUnavailable _ -> "base-commit-unavailable"
        | EmptyRequests -> "empty-requests"
        | InvalidTimelineSequence -> "invalid-timeline-sequence"
        | DuplicateTransaction _ -> "duplicate-transaction"
