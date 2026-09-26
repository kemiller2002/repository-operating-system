namespace Ros.Tests

open System
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module ReconciliationTests =
    let private actor =
        { ActorKind = "agent"; ActorId = "openai"; Provider = Some "openai"; Model = None; Runtime = None }

    let private envelope =
        { SchemaVersion = "1.0"
          TransactionId = "tx-1"
          WorkItem = "WI-0064"
          Branch = "WI-0064"
          BaseCommit = String.replicate 40 "a"
          Agent = actor
          Timeline = [{ Sequence = 1; Timestamp = DateTimeOffset.Parse("2026-09-26T10:00:00Z"); Action = "start" }]
          Requests = [{ RequestType = "work.start" }] }

    let private observed =
        { ActualBranch = "WI-0064"; HeadCommit = String.replicate 40 "b"; BaseCommitExists = true; TransactionAlreadyApplied = false }

    let tests =
        [ { Name = "reconciliation accepts a legal envelope"
            Run = fun () -> Assert.equal Accept (Reconciliation.decide envelope observed) }
          { Name = "reconciliation rejects claimed work-item branch mismatch"
            Run = fun () ->
                match Reconciliation.decide { envelope with Branch = "main" } observed with
                | Reject findings -> Assert.isTrue (List.contains (WorkItemBranchMismatch("WI-0064", "main")) findings) "missing mismatch finding"
                | value -> failwithf "expected rejection, got %A" value }
          { Name = "reconciliation rejects spoofed observed branch"
            Run = fun () ->
                match Reconciliation.decide envelope { observed with ActualBranch = "main" } with
                | Reject findings -> Assert.isTrue (List.contains (ObservedBranchMismatch("WI-0064", "main")) findings) "missing observed mismatch"
                | value -> failwithf "expected rejection, got %A" value }
          { Name = "reconciliation treats replay as already applied"
            Run = fun () -> Assert.equal AlreadyApplied (Reconciliation.decide envelope { observed with TransactionAlreadyApplied = true }) }
          { Name = "reconciliation rejects a discontinuous timeline"
            Run = fun () ->
                let changed = { envelope with Timeline = [{ envelope.Timeline.Head with Sequence = 2 }] }
                match Reconciliation.decide changed observed with
                | Reject findings -> Assert.isTrue (List.contains InvalidTimelineSequence findings) "missing sequence finding"
                | value -> failwithf "expected rejection, got %A" value } ]
