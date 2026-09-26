namespace Ros.Cli

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Ros.Application.Work
open Ros.Domain.Work
open Ros.Infrastructure.Git
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module ReconciliationCommands =
    let private hashEnvelope envelope =
        let bytes = JsonSerializer.SerializeToUtf8Bytes envelope
        Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    let private observe root (store: ReconciliationStore) (envelope: ReconciliationEnvelope) =
        let branch, head = ProcessGitRepository.readBranchAndCommit root
        { ActualBranch = branch |> Option.defaultValue ""
          HeadCommit = head |> Option.defaultValue ""
          BaseCommitExists = ProcessGitRepository.commitExists root envelope.BaseCommit
          TransactionAlreadyApplied = store.IsApplied envelope.TransactionId }

    // Request dispatch is intentionally closed until each request type is mapped
    // to an existing Ros.Application operation. Unknown requests never become state.
    let private applyRequests (_: ReconciliationEnvelope) =
        Error [ "request-dispatch-not-configured" ]

    let reconcile root envelopePath =
        match ReconciliationEnvelopeJson.read envelopePath with
        | Error codes ->
            eprintfn "REJECT %s" (String.concat "," codes)
            2
        | Ok envelope ->
            let store = FileReconciliationStore.create root
            let effects =
                { Observe = observe root store
                  ApplyRequests = applyRequests
                  HashEnvelope = hashEnvelope
                  Clock = fun () -> DateTimeOffset.UtcNow }
            match ReconciliationOperations.reconcile store effects envelope with
            | ReconciliationOutcome.Applied (receipt: ReconciliationReceipt) ->
                printfn "APPLIED %s %s" receipt.TransactionId receipt.HeadCommit
                0
            | ReconciliationOutcome.NoOp (receipt: ReconciliationReceipt) ->
                printfn "NOOP %s" receipt.TransactionId
                0
            | ReconciliationOutcome.Rejected (receipt: ReconciliationReceipt) ->
                eprintfn "REJECT %s %s" receipt.TransactionId (String.concat "," receipt.FindingCodes)
                2

    let inbox root =
        let inputs = InputDocuments.inventory root
        for relative, path in inputs do
            printfn "%s\t%s" relative (Path.GetRelativePath(root, path))
        if inputs.IsEmpty then printfn "No pending input documents."
        0
