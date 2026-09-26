namespace Ros.Infrastructure.Work

open System
open System.IO
open System.Text.Json
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module ReconciliationEnvelopeJson =
    let private requiredString name (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String ->
            let text = value.GetString()
            if String.IsNullOrWhiteSpace text then Error ("missing-" + name) else Ok text
        | _ -> Error ("missing-" + name)

    let private optionalString name (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Some (value.GetString())
        | _ -> None

    let private parseAgent (root: JsonElement) =
        match root.TryGetProperty "agent" with
        | true, agent when agent.ValueKind = JsonValueKind.Object ->
            match requiredString "kind" agent, requiredString "id" agent with
            | Ok kind, Ok id ->
                Ok { ActorKind = kind
                     ActorId = id
                     Provider = optionalString "provider" agent
                     Model = optionalString "model" agent
                     Runtime = optionalString "runtime" agent }
            | Error code, _ | _, Error code -> Error code
        | _ -> Error "missing-agent"

    let private parseTimeline (root: JsonElement) =
        match root.TryGetProperty "timeline" with
        | true, timeline when timeline.ValueKind = JsonValueKind.Array ->
            timeline.EnumerateArray()
            |> Seq.map (fun item ->
                match item.TryGetProperty "sequence", item.TryGetProperty "timestamp", item.TryGetProperty "action" with
                | (true, sequence), (true, timestamp), (true, action)
                    when sequence.ValueKind = JsonValueKind.Number
                         && timestamp.ValueKind = JsonValueKind.String
                         && action.ValueKind = JsonValueKind.String ->
                    match sequence.TryGetInt32(), DateTimeOffset.TryParse(timestamp.GetString()) with
                    | (true, number), (true, instant) when not (String.IsNullOrWhiteSpace(action.GetString())) ->
                        Ok { Sequence = number; Timestamp = instant; Action = action.GetString() }
                    | _ -> Error "invalid-timeline-entry"
                | _ -> Error "invalid-timeline-entry")
            |> Seq.toList
            |> fun values ->
                match values |> List.tryPick (function Error code -> Some code | _ -> None) with
                | Some code -> Error code
                | None -> Ok (values |> List.choose (function Ok value -> Some value | _ -> None))
        | _ -> Error "missing-timeline"

    let private parseRequests (root: JsonElement) =
        match root.TryGetProperty "requests" with
        | true, requests when requests.ValueKind = JsonValueKind.Array ->
            requests.EnumerateArray()
            |> Seq.map (fun item ->
                match requiredString "type" item with
                | Ok requestType -> Ok { RequestType = requestType }
                | Error _ -> Error "invalid-request")
            |> Seq.toList
            |> fun values ->
                match values |> List.tryPick (function Error code -> Some code | _ -> None) with
                | Some code -> Error code
                | None -> Ok (values |> List.choose (function Ok value -> Some value | _ -> None))
        | _ -> Error "missing-requests"

    let private parse (root: JsonElement) =
        match requiredString "schemaVersion" root,
              requiredString "transactionId" root,
              requiredString "workItem" root,
              requiredString "branch" root,
              requiredString "baseCommit" root,
              parseAgent root,
              parseTimeline root,
              parseRequests root with
        | Ok schemaVersion, Ok transactionId, Ok workItem, Ok branch, Ok baseCommit, Ok agent, Ok timeline, Ok requests ->
            Ok { SchemaVersion = schemaVersion
                 TransactionId = transactionId
                 WorkItem = workItem
                 Branch = branch
                 BaseCommit = baseCommit
                 Agent = agent
                 Timeline = timeline
                 Requests = requests }
        | values ->
            let code =
                match values with
                | Error c, _, _, _, _, _, _, _
                | _, Error c, _, _, _, _, _, _
                | _, _, Error c, _, _, _, _, _
                | _, _, _, Error c, _, _, _, _
                | _, _, _, _, Error c, _, _, _
                | _, _, _, _, _, Error c, _, _
                | _, _, _, _, _, _, Error c, _
                | _, _, _, _, _, _, _, Error c -> c
                | _ -> "invalid-envelope-json"
            Error [ code ]

    let read path : Result<ReconciliationEnvelope, string list> =
        try
            if not (File.Exists path) then Error [ "envelope-not-found" ]
            else
                use document = JsonDocument.Parse(File.ReadAllText path)
                if document.RootElement.ValueKind <> JsonValueKind.Object then Error [ "invalid-envelope-json" ]
                else parse document.RootElement
        with
        | :? JsonException -> Error [ "invalid-envelope-json" ]
        | _ -> Error [ "envelope-read-failed" ]

[<RequireQualifiedAccess>]
module InputDocuments =
    let canonicalRelative = Path.Combine(".praxis", "inbox", "documents")
    let legacyRelative = "input-documents"

    let inventory root =
        [ canonicalRelative; legacyRelative ]
        |> List.collect (fun relative ->
            let path = Path.Combine(Path.GetFullPath root, relative)
            if Directory.Exists path then
                Directory.EnumerateFiles(path)
                |> Seq.filter (fun file -> not (String.Equals(Path.GetFileName file, "README.md", StringComparison.OrdinalIgnoreCase)))
                |> Seq.map (fun file -> relative, file)
                |> Seq.toList
            else [])
        |> List.sortBy snd
