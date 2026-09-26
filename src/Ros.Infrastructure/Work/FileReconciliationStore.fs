namespace Ros.Infrastructure.Work

open System
open System.IO
open System.Text.Json
open Ros.Domain.Work
open Ros.Application.Work

[<RequireQualifiedAccess>]
module FileReconciliationStore =
    let private options = JsonSerializerOptions(WriteIndented = true)

    let private safeId (value: string) =
        value
        |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' then c else '_')
        |> Seq.toArray
        |> String

    let private atomicWrite (path: string) (content: string) =
        let directory = Path.GetDirectoryName path
        Directory.CreateDirectory directory |> ignore
        let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        File.WriteAllText(temp, content)
        File.Move(temp, path, true)

    let private receiptPath root transactionId =
        Path.Combine(root, ".praxis", "reconciled", safeId transactionId + ".json")

    let private quarantinePath root transactionId =
        Path.Combine(root, ".praxis", "rejected", safeId transactionId + ".json")

    let create root : ReconciliationStore =
        let fullRoot = Path.GetFullPath root
        { IsApplied = fun transactionId ->
              let path = receiptPath fullRoot transactionId
              if not (File.Exists path) then false
              else
                  try
                      let receipt = JsonSerializer.Deserialize<ReconciliationReceipt>(File.ReadAllText path, options)
                      not (isNull (box receipt)) && receipt.Status = ReconciliationReceiptStatus.Applied
                  with _ -> false
          WriteReceipt = fun receipt ->
              receipt
              |> fun value -> JsonSerializer.Serialize(value, options)
              |> atomicWrite (receiptPath fullRoot receipt.TransactionId)
          Quarantine = fun envelope codes ->
              let value =
                  {| schemaVersion = envelope.SchemaVersion
                     transactionId = envelope.TransactionId
                     workItem = envelope.WorkItem
                     branch = envelope.Branch
                     findingCodes = codes |}
              JsonSerializer.Serialize(value, options)
              |> atomicWrite (quarantinePath fullRoot envelope.TransactionId) }
