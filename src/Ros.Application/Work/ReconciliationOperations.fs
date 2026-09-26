namespace Ros.Application.Work

open System
open Ros.Domain.Work

type ReconciliationStore = {
    IsApplied: string -> bool
    WriteReceipt: ReconciliationReceipt -> unit
    Quarantine: ReconciliationEnvelope -> string list -> unit
}

type ReconciliationEffects = {
    Observe: ReconciliationEnvelope -> ReconciliationObservation
    ApplyRequests: ReconciliationEnvelope -> Result<unit, string list>
    HashEnvelope: ReconciliationEnvelope -> string
    Clock: unit -> DateTimeOffset
}

type ReconciliationOutcome =
    | Applied of ReconciliationReceipt
    | Rejected of ReconciliationReceipt
    | NoOp of ReconciliationReceipt

[<RequireQualifiedAccess>]
module ReconciliationOperations =
    let reconcile (store: ReconciliationStore) (effects: ReconciliationEffects) (envelope: ReconciliationEnvelope) =
        let observed0 = effects.Observe envelope
        let observed = { observed0 with TransactionAlreadyApplied = store.IsApplied envelope.TransactionId }
        let hash = effects.HashEnvelope envelope
        let receipt status codes =
            { TransactionId = envelope.TransactionId
              WorkItem = envelope.WorkItem
              Branch = observed.ActualBranch
              HeadCommit = observed.HeadCommit
              EnvelopeHash = hash
              Status = status
              FindingCodes = codes
              ReconciledAt = effects.Clock() }

        match Reconciliation.decide envelope observed with
        | ReconciliationDecision.AlreadyApplied ->
            NoOp (receipt ReconciliationReceiptStatus.AlreadyApplied [])
        | ReconciliationDecision.Reject findings ->
            let codes = findings |> List.map Reconciliation.findingCode
            store.Quarantine envelope codes
            let value = receipt ReconciliationReceiptStatus.Rejected codes
            store.WriteReceipt value
            Rejected value
        | ReconciliationDecision.Accept ->
            match effects.ApplyRequests envelope with
            | Ok () ->
                let value = receipt ReconciliationReceiptStatus.Applied []
                store.WriteReceipt value
                Applied value
            | Error codes ->
                store.Quarantine envelope codes
                let value = receipt ReconciliationReceiptStatus.Rejected codes
                store.WriteReceipt value
                Rejected value
