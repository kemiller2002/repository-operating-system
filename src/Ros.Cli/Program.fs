module Ros.Cli.Program

open System
open System.Collections.Generic
open System.IO
open Ros.Application.Artifacts
open Ros.Application.Git
open Ros.Application.Work
open Ros.Application.Ordo
open Ros.Contracts.Cli
open Ros.Contracts.Git
open Ros.Contracts.Work
open Ros.Contracts.Ordo
open Ros.Domain.Artifacts
open Ros.Domain.Git
open Ros.Domain.Provenance
open Ros.Domain.Telemetry
open Ros.Domain.Work
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Git
open Ros.Infrastructure.Work
open Ros.Infrastructure.Ordo
open System.Text.Json
open System.Text.Json.Nodes
open Aegis

/// Single authoritative version, read from package.json at build time; see
/// Ros.Cli.Lifecycle.Version and Directory.Build.props.
let Version = Lifecycle.Version

let private usage =
    "Usage: ros-fs [--root PATH] version | artifacts validate [--json] | registry build [--dry-run] | registry check | git status [--json] | work decide [options] | work plan [options] [--resolve-telemetry --candidate EXECUTIONID=active|finalized]* [--requested-execution-id ID] | work context-plan [options] | work backlog-decide --state STATE --action ACTION [--reason TEXT] | work backlog-promotion-plan --id ID [--queue-state ID=STATE] [--type TYPE] | work validate [--json] | work backlog-validate [--json] | work backlog-transition --id ID --action {ready|block|abandon} --occurred-at TIMESTAMP [--reason TEXT] | work capture --title TITLE --occurred-at TIMESTAMP [--id ID] [--priority {high|medium|low}] [--description TEXT] [--tag TAG]* [--actor NAME] [--source NAME] [--source-reference REF] | work update --id ID --occurred-at TIMESTAMP [--title TEXT] [--description TEXT] [--priority {high|medium|low}] [--tag TAG]* | work attach --id ID --occurred-at TIMESTAMP --file PATH[=NAME] [--file PATH[=NAME]]* | work start --id ID [--id ID]* --occurred-at TIMESTAMP [--type TYPE] [--actor NAME] [--classification NAME]* | work resume --id ID [--id ID]* --occurred-at TIMESTAMP [--actor NAME] | work block --id ID [--id ID]* --occurred-at TIMESTAMP [--reason TEXT] [--actor NAME] | work complete --id ID [--id ID]* --occurred-at TIMESTAMP [--evidence TYPE=PATH]* [--conclusion TEXT] [--actor NAME] | telemetry adapters | telemetry show [TARGET] | telemetry summary|summarize [TARGET] | telemetry finalize [TARGET] [--quiet] | telemetry record [TARGET] --metric ID --value VALUE [--unit TEXT] [--currency TEXT] [--quality {observed|derived|estimated}] [--confidence VALUE] [--scope TEXT] [--source-type TEXT] [--source-name TEXT] [--mechanism TEXT] [--pricing-source TEXT] [--pricing-version TEXT] [--collected-at TIMESTAMP] [--quiet] | telemetry ingest [TARGET] --input FILE [--adapter NAME] [--quiet] | telemetry classify [TARGET] --classification NAME [--classification NAME]* [--rationale TEXT] [--evidence-link LINK]* [--rd-context FILE] [--quiet] | telemetry start WORKITEMID [--classification NAME]* [--classification-rationale TEXT] [--quiet] | adapter call --store FILE --request FILE | foundations verify [--json] | adapter publish --target FILE | ordo ingest --input FILE | ordo assess --input FILE | ordo observe-search --input FILE | ordo observe-effect --input FILE | ordo current | ordo handoff --revision REV --source SOURCE [--fact TEXT]* [--assumption TEXT]* [--unknown TEXT]* [--obligation TEXT]* [--next-action TEXT]* | provenance identity [--json] [IDENTITY] | provenance record (--path PATH|--id ID) --operation {created|modified|reviewed|approved|superseded|migrated} [--reason TEXT] [--evidence REF]* [--derived-from REF]* [--execution EXE-ID] [--occurred-at TIMESTAMP] [--json] | provenance show ID|PATH [--json] | provenance audit [--json]; IDENTITY (work start/resume/block/complete, add, telemetry start): [--actor-kind {agent|human|automation|unknown|x-...}] [--agent ID|--actor ID] [--provider P] [--model M] [--runtime R] ..."

/// Removes one global `--name VALUE` option from the argument list wherever
/// it appears, so the command parsers below only ever see their own flags.
let private takeGlobalOption (name: string) (values: ResizeArray<string>) : Result<string option, string> =
    let index = values.IndexOf(name)

    if index < 0 then
        Ok None
    elif index + 1 >= values.Count || values[index + 1].StartsWith("--", StringComparison.Ordinal) then
        Error $"{name} requires a value"
    else
        let value = values[index + 1]
        values.RemoveAt(index + 1)
        values.RemoveAt(index)
        Ok(Some value)

let private parseGlobals (arguments: string array) =
    let values = ResizeArray<string>(arguments)

    match takeGlobalOption "--root" values with
    | Error message -> Error message
    | Ok root ->
        match takeGlobalOption "--package-root" values with
        | Error message -> Error message
        | Ok packageRoot ->
            let resolvedRoot =
                root |> Option.map Path.GetFullPath |> Option.defaultWith (fun () -> Path.GetFullPath(Directory.GetCurrentDirectory()))

            Ok(resolvedRoot, packageRoot |> Option.map Path.GetFullPath, values |> Seq.toList)

let private renderFinding (finding: ArtifactFinding) =
    let location =
        if String.IsNullOrEmpty finding.Field then finding.Path else $"{finding.Path}:{finding.Field}"

    $"{location}: {finding.Message}"

let private dependencyFailureMessage (failure: DependencyFailure) : string =
    let location = failure.Path |> Option.map (fun path -> $" '{path}'") |> Option.defaultValue ""

    let outcome =
        match failure.Outcome with
        | DependencyOutcome.Failed -> "failed"
        | DependencyOutcome.Indeterminate -> "indeterminate"

    $"{failure.Operation}{location} {outcome}: {failure.Message}"

let private reportDependencyFailure (failure: DependencyFailure) =
    eprintfn "ERROR %s" (dependencyFailureMessage failure)
    1

let private runValidation asJson repository =
    match ArtifactOperations.validate repository with
    | ValidationOutcome.DependencyFailure failure -> reportDependencyFailure failure
    | ValidationOutcome.Completed findings when asJson ->
        printf "%s" (FindingContract.renderJson findings)
        if findings.IsEmpty then 0 else 1
    | ValidationOutcome.Completed [] ->
        printfn "validation passed"
        0
    | ValidationOutcome.Completed findings ->
        for finding in findings do
            eprintfn "ERROR %s\n  REPAIR %s" (renderFinding finding) (FindingContract.repair finding)

        eprintfn "validation failed with %d error(s)" findings.Length
        1

let private runRegistryCheck repository =
    match ArtifactOperations.checkRegistries repository with
    | RegistryCheckOutcome.DependencyFailure failure -> reportDependencyFailure failure
    | RegistryCheckOutcome.Completed [] ->
        printfn "registries are current"
        0
    | RegistryCheckOutcome.Completed findings ->
        for finding in findings do
            eprintfn "ERROR %s" (renderFinding finding)
        1

let private runRegistryBuild dryRun repository =
    let reportChange (change: RegistryChange) =
        printfn "%s %s" (if dryRun then "WOULD WRITE" else "WROTE") change.Path

    match ArtifactOperations.buildRegistries dryRun repository with
    | RegistryBuildOutcome.DependencyFailure failure -> reportDependencyFailure failure
    | RegistryBuildOutcome.Rejected findings ->
        for finding in findings do
            eprintfn "ERROR %s" (renderFinding finding)
        1
    | RegistryBuildOutcome.Completed changes ->
        changes |> List.iter reportChange
        printfn "%d registry file(s) %s" changes.Length (if dryRun then "would change" else "changed")
        0
    | RegistryBuildOutcome.Incomplete(written, pending, failure) ->
        written |> List.iter reportChange
        reportDependencyFailure failure |> ignore
        eprintfn "registry build incomplete: %d written; %d pending" written.Length pending.Length
        1

let private renderGitChange change =
    match change.OriginalPath with
    | Some originalPath -> $"{GitStatus.code change.Status} {originalPath} -> {change.Path}"
    | None -> $"{GitStatus.code change.Status} {change.Path}"

let private runGitStatus asJson repository =
    let observation = GitOperations.observe repository

    if asJson then
        printf "%s" (GitStatusContract.renderJson observation)

    match observation with
    | GitStatusObservation.Clean ->
        if not asJson then printfn "working tree clean"
        0
    | GitStatusObservation.Changed changes ->
        if not asJson then changes |> List.iter (renderGitChange >> printfn "%s")
        0
    | GitStatusObservation.Unavailable failure ->
        if not asJson then eprintfn "ERROR %s unavailable: %s" failure.Operation failure.Message
        1

let private optionValue name arguments =
    arguments
    |> List.tryFindIndex ((=) name)
    |> Option.bind (fun index -> arguments |> List.tryItem (index + 1))

let private optionValues name arguments =
    arguments
    |> List.mapi (fun index value -> index, value)
    |> List.choose (fun (index, value) -> if value = name then arguments |> List.tryItem (index + 1) else None)

/// Positional tokens left over after removing every `flag value` pair whose
/// flag is in `flagsWithValues`. Used to detect production's positional-ID
/// syntax (e.g. `work ready ID1 ID2`) where this CLI's own convention is a
/// repeated `--id` flag instead, so a caller using that syntax gets a clear
/// redirect rather than a silently wrong read-only result.
let rec private residualPositionalArgs (flagsWithValues: Set<string>) (arguments: string list) =
    match arguments with
    | flag :: _ :: rest when flagsWithValues.Contains flag -> residualPositionalArgs flagsWithValues rest
    | token :: rest -> token :: residualPositionalArgs flagsWithValues rest
    | [] -> []

let private parseWorkState value =
    match value with
    | "ready" -> Some LiveWorkState.Ready
    | "active" -> Some LiveWorkState.Active
    | "blocked" -> Some LiveWorkState.Blocked
    | "complete" -> Some LiveWorkState.Complete
    | _ -> None

let private parseWorkAction value =
    match value with
    | "begin" -> Some WorkAction.Begin
    | "block" -> Some WorkAction.Block
    | "resume" -> Some WorkAction.Resume
    | "complete" -> Some WorkAction.Complete
    | _ -> None

let private parseBacklogState value =
    match value with
    | "captured" -> Some BacklogState.Captured
    | "ready" -> Some BacklogState.Ready
    | "blocked" -> Some BacklogState.Blocked
    | "abandoned" -> Some BacklogState.Abandoned
    | _ -> None

let private parseBacklogAction value =
    match value with
    | "ready" -> Some BacklogAction.Ready
    | "block" -> Some BacklogAction.Block
    | "abandon" -> Some BacklogAction.Abandon
    | "start" -> Some BacklogAction.Start
    | _ -> None

let private runWorkDecision arguments =
    let state = optionValue "--state" arguments |> Option.bind parseWorkState
    let action = optionValue "--action" arguments |> Option.bind parseWorkAction

    match state, action with
    | Some current, Some requested ->
        let request =
            { State = current
              Action = requested
              BlockReason = optionValue "--reason" arguments
              RequiredEvidence = optionValues "--required" arguments |> Set.ofList
              ProvidedEvidence = optionValues "--provided" arguments |> Set.ofList }

        let decision = WorkOperations.decideTransition request
        printf "%s" (WorkDecisionContract.renderJson decision)

        match decision with
        | TransitionDecision.Allowed _ -> 0
        | TransitionDecision.Rejected _ -> 1
    | _ ->
        eprintfn "ERROR work decide requires a valid --state and --action"
        2

let private parseEvidence (value: string) : WorkEvidence option =
    match value.Split('=', 2) with
    | [| evidenceType; path |] when evidenceType.Length > 0 && path.Length > 0 ->
        Some
            { Type = evidenceType
              Path = path }
    | _ -> None

let private parseCandidate workItemId (value: string) : ExecutionLinkCandidate option =
    match value.Split('=', 2) with
    | [| executionId; "active" |] when executionId.Length > 0 ->
        Some
            { ExecutionId = executionId
              WorkItemId = workItemId
              Status = ExecutionStatus.Active }
    | [| executionId; "finalized" |] when executionId.Length > 0 ->
        Some
            { ExecutionId = executionId
              WorkItemId = workItemId
              Status = ExecutionStatus.Finalized }
    | _ -> None

let private defaultLocalState (action: WorkAction) =
    match action with
    | WorkAction.Begin
    | WorkAction.Resume -> "active"
    | WorkAction.Block -> "blocked"
    | WorkAction.Complete -> "complete"

let private runWorkPlan root arguments =
    let state = optionValue "--state" arguments |> Option.bind parseWorkState
    let action = optionValue "--action" arguments |> Option.bind parseWorkAction
    let workItem = optionValue "--id" arguments
    let workType = optionValue "--type" arguments
    let occurredAt = optionValue "--occurred-at" arguments
    let currentEvidence = optionValues "--current-evidence" arguments |> List.map parseEvidence
    let providedEvidence = optionValues "--evidence" arguments |> List.map parseEvidence

    match state, action, workItem, workType, occurredAt with
    | Some current, Some requested, Some workItemId, Some itemType, Some timestamp
        when currentEvidence |> List.forall Option.isSome
             && providedEvidence |> List.forall Option.isSome ->
        let request =
            { Item =
                { Id = workItemId
                  WorkType = itemType
                  LocalState = optionValue "--local-state" arguments |> Option.defaultValue (WorkDecisionContract.stateName current)
                  SemanticState = current
                  Evidence = currentEvidence |> List.choose id
                  BlockReason = optionValue "--current-block-reason" arguments
                  UpdatedAt = optionValue "--updated-at" arguments
                  CompletedAt = optionValue "--completed-at" arguments
                  TelemetryExecutionIds = optionValues "--telemetry-id" arguments }
              Action = requested
              TargetLocalState = optionValue "--target-local-state" arguments |> Option.defaultValue (defaultLocalState requested)
              BlockReason = optionValue "--reason" arguments
              RequiredEvidence = optionValues "--required" arguments |> Set.ofList
              ProvidedEvidence = providedEvidence |> List.choose id
              Repository = optionValue "--repository" arguments |> Option.defaultValue "repository"
              ProtocolVersion = optionValue "--protocol-version" arguments |> Option.defaultValue "1.0.0"
              OccurredAt = timestamp
              ChangedPaths = optionValues "--path" arguments
              TelemetryEnabled = arguments |> List.contains "--telemetry-enabled" }

        if arguments |> List.contains "--resolve-telemetry" then
            let explicitCandidates = optionValues "--candidate" arguments |> List.map (parseCandidate workItemId)

            if explicitCandidates |> List.forall Option.isSome then
                match WorkOperations.planTransition request with
                | WorkPlanOutcome.Rejected rejection ->
                    printf "%s" (WorkPlanContract.renderJson (WorkPlanOutcome.Rejected rejection))
                    1
                | WorkPlanOutcome.Planned plan ->
                    let repository: TelemetryStateRepository =
                        { Observe =
                            fun observedWorkItemId ->
                                { LinkedExecutionIds = plan.Item.TelemetryExecutionIds
                                  Candidates =
                                    if explicitCandidates.IsEmpty then
                                        FileTelemetryStateRepository.readCandidates root observedWorkItemId
                                    else
                                        explicitCandidates |> List.choose id
                                  RequestedExecutionId = optionValue "--requested-execution-id" arguments } }

                    let outcome = WorkOperations.resolveTelemetry repository plan
                    printf "%s" (WorkPlanContract.renderResolvedTelemetryJson outcome)

                    match outcome with
                    | ResolvedTelemetryOutcome.Resolved _ -> 0
                    | ResolvedTelemetryOutcome.PendingNewExecution _
                    | ResolvedTelemetryOutcome.Rejected _ -> 1
            else
                eprintfn "ERROR work plan --resolve-telemetry requires EXECUTIONID=active|finalized for every --candidate"
                2
        elif arguments |> List.contains "--verify-evidence" then
            let outcome = WorkOperations.planVerifiedTransition (FileEvidenceRepository.create root) request
            printf "%s" (WorkPlanContract.renderVerifiedJson outcome)

            match outcome with
            | VerifiedWorkPlanOutcome.Planned _ -> 0
            | VerifiedWorkPlanOutcome.TransitionRejected _
            | VerifiedWorkPlanOutcome.EvidenceRejected _ -> 1
        else
            let outcome = WorkOperations.planTransition request
            printf "%s" (WorkPlanContract.renderJson outcome)

            match outcome with
            | WorkPlanOutcome.Planned _ -> 0
            | WorkPlanOutcome.Rejected _ -> 1
    | _ ->
        eprintfn "ERROR work plan requires valid --id, --type, --state, --action, --occurred-at, and TYPE=PATH evidence"
        2

/// Mirrors production `gitPaths` (`tools/ros_cli.mjs`): real working-tree
/// changed paths plus, when `$ROS_BASE_REF` resolves to an existing commit,
/// its committed-range diff against `HEAD` — deduped and ordinally sorted.
/// A non-repository directory yields no paths (greenfield compatibility); any
/// other Git or base-ref-diff failure is an error, never a silent empty list.
let private realObservedGitPaths root : Result<string list, GitFailure> =
    let workingTreePaths =
        match GitOperations.observe (ProcessGitRepository.create root) with
        | GitStatusObservation.Clean -> Ok []
        | GitStatusObservation.Changed changes -> Ok(changes |> List.map _.Path)
        | GitStatusObservation.Unavailable failure when failure.Reason = GitUnavailableReason.NotRepository -> Ok []
        | GitStatusObservation.Unavailable failure -> Error failure

    workingTreePaths
    |> Result.bind (fun paths ->
        let baseRef =
            match Environment.GetEnvironmentVariable "ROS_BASE_REF" with
            | null
            | "" -> None
            | value -> Some value

        match GitOperations.compareBase (ProcessGitRepository.createBaseComparison root) baseRef with
        | GitBaseComparisonOutcome.NotConfigured
        | GitBaseComparisonOutcome.RefUnavailable -> Ok paths
        | GitBaseComparisonOutcome.Committed committedPaths -> Ok(paths @ committedPaths)
        | GitBaseComparisonOutcome.Unavailable failure -> Error failure)
    |> Result.map (fun paths -> paths |> List.distinct |> List.sortWith (fun left right -> String.CompareOrdinal(left, right)))

let private formatGitFailure (failure: GitFailure) = $"{failure.Operation} unavailable: {failure.Message}"

let private runWorkContextPlan root arguments =
    let action = optionValue "--action" arguments |> Option.bind parseWorkAction
    let contextPath = optionValue "--context" arguments
    let occurredAt = optionValue "--occurred-at" arguments
    let providedEvidence = optionValues "--evidence" arguments |> List.map parseEvidence

    match action, contextPath, occurredAt with
    | Some requested, Some relativeContextPath, Some timestamp when providedEvidence |> List.forall Option.isSome ->
        let fullContextPath = Path.GetFullPath(Path.Combine(root, relativeContextPath))

        match WorkContextPlanContract.parseJson (File.ReadAllText fullContextPath) with
        | Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok context ->
            let explicitObservedGitPaths = optionValues "--observed-git-path" arguments
            let explicitMeaningfulChangedPaths = optionValues "--path" arguments
            let autoMode = explicitObservedGitPaths.IsEmpty && explicitMeaningfulChangedPaths.IsEmpty

            // Mirrors production's own gate on calling `gitPaths` at all:
            // only completion and a repository's first `begin` observe Git.
            let shouldObserveGit =
                requested = WorkAction.Complete
                || (requested = WorkAction.Begin && context.StartedAt.IsNone)

            let observedGitPathsResult =
                if not autoMode then Ok explicitObservedGitPaths
                elif shouldObserveGit then realObservedGitPaths root
                else Ok []

            match observedGitPathsResult with
            | Error failure ->
                eprintfn "ERROR %s" (formatGitFailure failure)
                1
            | Ok observedGitPaths ->
                let meaningfulChangedPaths =
                    if not autoMode then
                        explicitMeaningfulChangedPaths
                    elif shouldObserveGit then
                        PathFilter.meaningfulPaths (FileWorkConfigRepository.readPathFilterConfig root) observedGitPaths
                    else
                        []

                let request =
                    { Context = context
                      Action = requested
                      WorkItemIds = optionValues "--id" arguments
                      NewItemType = optionValue "--type" arguments |> Option.defaultValue "task"
                      TargetLocalState = optionValue "--target-local-state" arguments |> Option.defaultValue (defaultLocalState requested)
                      BlockReason = optionValue "--reason" arguments
                      DefaultRequiredEvidence = optionValues "--required" arguments |> Set.ofList
                      RequiredEvidenceByType = Map.empty
                      ProvidedEvidence = providedEvidence |> List.choose id
                      Repository = optionValue "--repository" arguments |> Option.defaultValue "repository"
                      ProtocolVersion = optionValue "--protocol-version" arguments |> Option.defaultValue "1.0.0"
                      Actor = optionValue "--actor" arguments |> Option.defaultValue "unknown"
                      OccurredAt = timestamp
                      MeaningfulChangedPaths = meaningfulChangedPaths
                      ObservedGitPaths = observedGitPaths
                      TelemetryEnabled = arguments |> List.contains "--telemetry-enabled" }

                if arguments |> List.contains "--verify-evidence" then
                    let outcome = WorkOperations.planVerifiedContext (FileEvidenceRepository.create root) request
                    printf "%s" (WorkContextPlanContract.renderVerifiedJson outcome)

                    match outcome with
                    | VerifiedWorkContextPlanOutcome.Planned _ -> 0
                    | VerifiedWorkContextPlanOutcome.ContextRejected _
                    | VerifiedWorkContextPlanOutcome.EvidenceRejected _ -> 1
                else
                    let outcome = WorkOperations.planContext request
                    printf "%s" (WorkContextPlanContract.renderJson outcome)

                    match outcome with
                    | WorkContextPlanOutcome.Planned _ -> 0
                    | WorkContextPlanOutcome.Rejected _ -> 1
    | _ ->
        eprintfn "ERROR work context-plan requires valid --context, --id, --action, --occurred-at, and TYPE=PATH evidence"
        2

let private runBacklogDecision arguments =
    let state = optionValue "--state" arguments |> Option.bind parseBacklogState
    let action = optionValue "--action" arguments |> Option.bind parseBacklogAction

    match state, action with
    | Some current, Some requested ->
        let outcome =
            WorkOperations.decideBacklogTransition
                { State = current
                  Action = requested
                  Reason = optionValue "--reason" arguments }

        printf "%s" (BacklogContract.renderDecisionJson outcome)

        match outcome with
        | BacklogTransitionDecision.Allowed _ -> 0
        | BacklogTransitionDecision.Rejected _ -> 1
    | _ ->
        eprintfn "ERROR work backlog-decide requires valid --state and --action"
        2

let private parseQueueState (value: string) =
    match value.Split('=', 2) with
    | [| workItemId; state |] -> parseBacklogState state |> Option.map (fun parsed -> workItemId, parsed)
    | _ -> None

let private runBacklogPromotionPlan arguments =
    let states = optionValues "--queue-state" arguments |> List.map parseQueueState

    if states |> List.forall Option.isSome then
        let outcome =
            WorkOperations.planBacklogPromotion
                { WorkItemIds = optionValues "--id" arguments
                  QueueStates = states |> List.choose id |> Map.ofList
                  WorkType = optionValue "--type" arguments |> Option.defaultValue "task" }

        printf "%s" (BacklogContract.renderPromotionJson outcome)

        match outcome with
        | BacklogPromotionOutcome.Planned _ -> 0
        | BacklogPromotionOutcome.Rejected _ -> 1
    else
        eprintfn "ERROR --queue-state requires ID=STATE with a valid backlog state"
        2

let private readWorkContext root : Result<WorkContextPlanningView, string> =
    let path = Path.Combine(root, ".ros", "context", "current.json")

    if not (File.Exists path) then
        Ok { WorkItems = []; StartedAt = None; BaselineDirtyPaths = [] }
    else
        WorkContextPlanContract.parseJson (File.ReadAllText path)

/// Mirrors production `workFindings` (`tools/ros_cli.mjs`): when attribution
/// enforcement is off, no Git observation is ever attempted. Otherwise a
/// broken Git repository yields a single synthetic finding rather than
/// failing outright, matching Node's `error.gitFailure` catch. Shared by
/// the standalone `work validate` diagnostic and the unified `validate`
/// command, since both need production's exact same findings.
let private computeWorkAttributionFindings root : Result<WorkAttributionFinding list, string> =
    let enforce = FileWorkConfigRepository.readEnforceAttribution root

    if not enforce then
        Ok []
    else
        match readWorkContext root with
        | Error message -> Error message
        | Ok context ->
            match realObservedGitPaths root with
            | Error failure ->
                Ok
                    [ { Path = ".git"
                        Field = "work_items"
                        Message = $"cannot verify work attribution because {failure.Operation} is unavailable: {GitUnavailableReason.code failure.Reason}" } ]
            | Ok observedGitPaths ->
                let request =
                    { Enforce = true
                      ObservedGitPaths = observedGitPaths
                      PathFilterConfig = FileWorkConfigRepository.readPathFilterConfig root
                      BaselineDirtyPaths = context.BaselineDirtyPaths
                      AttributedPaths = FileEventLogRepository.readAttributedPaths root
                      HasActiveOrBlockedWork =
                        context.WorkItems
                        |> List.exists (fun item -> item.SemanticState = LiveWorkState.Active || item.SemanticState = LiveWorkState.Blocked) }

                Ok(WorkAttribution.findings request)

let private runWorkAttributionValidate root arguments =
    if not (arguments |> List.forall ((=) "--json")) then
        eprintfn "%s" usage
        2
    else
        match computeWorkAttributionFindings root with
        | Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok findings ->
            printf "%s" (WorkAttributionContract.renderJson findings)
            if findings.IsEmpty then 0 else 1

/// Shared by `work backlog-transition` and `work block` (whose backlog-item
/// branch is the same effect): mirrors production `backlogTransitionUnlocked`
/// for one id, without acquiring or releasing any lock itself -- the caller
/// already holds `work-protocol` across the whole operation, exactly like
/// production's own `withWorkProtocol`-wrapped callers never re-acquire it
/// per id.
let private applyBacklogTransition
    root
    (id: string)
    (action: BacklogAction)
    (actionText: string)
    (reason: string option)
    (timestamp: string)
    (contextItems: LiveWorkItem list)
    : Result<BacklogQueueRow, string> =
    match FileBacklogQueueRepository.readItems root |> List.tryFind (fun item -> item.Id = id) with
    | None -> Error $"'{id}' is not a captured local work item"
    | Some item ->
        let illegalTransition () = Error $"cannot {actionText} backlog item '{id}' from '{item.Status}'"

        match parseBacklogState item.Status with
        | None -> illegalTransition ()
        | Some currentState ->
            match WorkOperations.decideBacklogTransition { State = currentState; Action = action; Reason = reason } with
            | BacklogTransitionDecision.Rejected(BacklogTransitionRejection.IllegalTransition _) -> illegalTransition ()
            | BacklogTransitionDecision.Rejected BacklogTransitionRejection.BlockReasonRequired -> Error "block requires --reason"
            | BacklogTransitionDecision.Allowed BacklogTransitionEffect.PromoteToLiveWork ->
                Error "this command does not support 'start'; use production './ros work start'"
            | BacklogTransitionDecision.Allowed(BacklogTransitionEffect.ChangeState(newState, blockedChange, abandonedChange)) ->
                FileBacklogQueueRepository.applyStateChange root id newState blockedChange abandonedChange timestamp contextItems

/// Mirrors production `backlogTransition`/`backlogTransitionUnlocked`
/// (`tools/ros_cli.mjs`): a real effect on `.ros/work/queue.json` and
/// `.ros/work/queue.md`, guarded by the same "work-protocol" file lock and
/// backlog-state recovery journal production's own writer uses. Only the
/// three backlog-only actions (`ready`/`block`/`abandon`) are supported --
/// `start` promotes a backlog item into live work and is production's own
/// `startWork`, a materially larger effect (live context + telemetry) this
/// slice deliberately excludes.
let private runBacklogTransitionEffect root arguments =
    let workItemId = optionValue "--id" arguments
    let rawAction = optionValue "--action" arguments
    let occurredAt = optionValue "--occurred-at" arguments
    let reason = optionValue "--reason" arguments

    match workItemId, rawAction, rawAction |> Option.bind parseBacklogAction, occurredAt with
    | Some id, Some actionText, Some action, Some timestamp ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match readWorkContext root with
                            | Error message -> Error message
                            | Ok context -> applyBacklogTransition root id action actionText reason timestamp context.WorkItems
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok row ->
                printf "%s" (BacklogTransitionEffectContract.renderJson row)
                0
    | _ ->
        eprintfn "ERROR work backlog-transition requires valid --id, --action, and --occurred-at"
        2

/// Mirrors production `work context [ID]` (`contextView`,
/// `tools/ros_cli.mjs`): the first pure read-only view of
/// `.ros/context/current.json` this CLI exposes, requiring no lock at all
/// (unlike every mutation command above). `ID` is positional, like
/// `telemetry show`'s own `TARGET`; a supplied ID that matches no context
/// item rejects with production's exact message rather than returning an
/// empty view.
let private runWorkContext root (arguments: string list) =
    let requestedId = arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))

    match FileWorkContextRepository.readContextView root requestedId with
    | Error message ->
        eprintfn "ERROR %s" message
        1
    | Ok view ->
        printf "%s" (view.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
        0

let private attachmentSummaryNode (attachment: WorkAttachmentSummary) : JsonObject =
    let node = JsonObject()
    node["id"] <- JsonValue.Create attachment.Id
    node["name"] <- JsonValue.Create attachment.Name
    node["size"] <- JsonValue.Create attachment.Size

    node["contentType"] <-
        match attachment.ContentType with
        | Some contentType -> JsonValue.Create contentType :> JsonNode
        | None -> null

    node["uploadedAt"] <- JsonValue.Create attachment.UploadedAt
    node

/// Same field key order production's own `mergedRows` object literal
/// (`tools/ros_cli.mjs`): `id, title, description, tags, priority, status,
/// blockedReason, backlogActions, attachments, liveWorkItem`. Unlike
/// `description`/`priority` (always present, `null` when absent),
/// `blockedReason` is genuinely omitted when neither source names one --
/// production's own object literal leaves it `undefined`, which
/// `JSON.stringify` drops entirely rather than writing `null`.
let private workListRowNode (row: WorkListRow) : JsonObject =
    let node = JsonObject()
    node["id"] <- JsonValue.Create row.Id
    node["title"] <- JsonValue.Create row.Title

    node["description"] <-
        match row.Description with
        | Some description -> JsonValue.Create description :> JsonNode
        | None -> null

    let tags = JsonArray()
    row.Tags |> List.iter (fun tag -> tags.Add(JsonValue.Create tag: JsonNode))
    node["tags"] <- tags

    node["priority"] <-
        match row.Priority with
        | Some priority -> JsonValue.Create priority :> JsonNode
        | None -> null

    node["status"] <- JsonValue.Create row.Status

    match row.BlockedReason with
    | Some reason -> node["blockedReason"] <- JsonValue.Create reason
    | None -> ()

    let backlogActions = JsonArray()
    row.BacklogActions |> List.iter (fun action -> backlogActions.Add(JsonValue.Create action: JsonNode))
    node["backlogActions"] <- backlogActions

    let attachments = JsonArray()
    row.Attachments |> List.iter (fun attachment -> attachments.Add(attachmentSummaryNode attachment: JsonNode))
    node["attachments"] <- attachments

    node["liveWorkItem"] <-
        match row.LiveWorkItem with
        | None -> null
        | Some live ->
            let liveNode = JsonObject()
            liveNode["state"] <- JsonValue.Create live.State
            liveNode["semanticState"] <- JsonValue.Create live.SemanticState
            let allowedActions = JsonArray()
            live.AllowedActions |> List.iter (fun action -> allowedActions.Add(JsonValue.Create action: JsonNode))
            liveNode["allowedActions"] <- allowedActions
            liveNode :> JsonNode

    node

let private renderWorkListRows (rows: WorkListRow list) =
    let array = JsonArray()
    rows |> List.iter (fun row -> array.Add(workListRowNode row: JsonNode))
    array.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2))

/// Mirrors production `work`/`work list` (`mergedWorkView(root, {})`,
/// `tools/ros_cli.mjs`): every id known to the backlog queue or the live
/// context, merged and augmented exactly as `Ros.Domain.Work.WorkListView`
/// decides. `--tag`/`--status` filtering is not yet ported -- a known gap,
/// documented alongside this increment's docs update.
/// Mirrors production `mergedWorkView`'s own `--tag`/`--status` filters
/// (`tools/ros_cli.mjs`): every requested tag must be present (`--tag`
/// repeatable, AND semantics), and `--status` matches a row's `status`
/// field exactly. Filtering happens here, over `readListView`'s already-real
/// merged rows, matching production's own wrapper-over-`mergedRows` shape.
let private runWorkList root (arguments: string list) =
    match FileWorkListRepository.readListView root with
    | Error message ->
        eprintfn "ERROR %s" message
        1
    | Ok rows ->
        let tags = optionValues "--tag" arguments
        let status = optionValue "--status" arguments

        let filtered =
            rows
            |> List.filter (fun row -> tags |> List.forall (fun tag -> List.contains tag row.Tags))
            |> List.filter (fun row ->
                match status with
                | Some expected -> row.Status = expected
                | None -> true)

        printf "%s" (renderWorkListRows filtered)
        0

/// Mirrors production `showWork` (`tools/ros_cli.mjs`): the same merged
/// row `work list` produces for one id, plus the detail markdown file's
/// content (`null` when none exists) -- an unknown id rejects with
/// production's exact message rather than an empty view.
let private runWorkShow root (arguments: string list) =
    match arguments |> List.tryHead with
    | None ->
        eprintfn "ERROR show requires an ID"
        1
    | Some id ->
        match FileWorkListRepository.readShowView root id with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok(row, detail) ->
            let node = workListRowNode row

            node["detail"] <-
                match detail with
                | Some text -> JsonValue.Create text :> JsonNode
                | None -> null

            printf "%s" (node.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
            0

/// Mirrors production `captureWorkUnlocked`/`nextQueueId`
/// (`tools/ros_cli.mjs`) via `Ros.Domain.Work.WorkCapture`, excluding
/// `--file` attachment (a separate, larger effect). A real effect,
/// guarded by the same "work-protocol" lock and `backlog-state` recovery
/// journal as `work backlog-transition`.
let private runWorkCapture root arguments (createdByActor: Actor) =
    let title = optionValue "--title" arguments
    let occurredAt = optionValue "--occurred-at" arguments

    match title, occurredAt with
    | Some titleText, Some timestamp ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match readWorkContext root with
                            | Error message -> Error message
                            | Ok context ->
                                let actor =
                                    optionValue "--actor" arguments
                                    |> Option.orElse (Environment.GetEnvironmentVariable "ROS_ACTOR" |> Option.ofObj)
                                    |> Option.defaultValue "unknown"

                                let request: WorkCaptureRequest =
                                    { Title = titleText
                                      ExplicitId = optionValue "--id" arguments
                                      Priority = optionValue "--priority" arguments
                                      Description = optionValue "--description" arguments
                                      Tags = optionValues "--tag" arguments
                                      Actor = actor
                                      Source = optionValue "--source" arguments
                                      SourceReference = optionValue "--source-reference" arguments
                                      ExistingQueueIds =
                                        FileBacklogQueueRepository.readItems root
                                        |> List.map (fun item -> item.Id)
                                        |> Set.ofList
                                      ExistingContextIds = context.WorkItems |> List.map (fun item -> item.Id) |> Set.ofList
                                      NextSeq = FileBacklogQueueRepository.readNextSeq root
                                      OccurredAt = timestamp }

                                match WorkCapture.plan request with
                                | WorkCaptureOutcome.Rejected rejection ->
                                    Error(
                                        match rejection with
                                        | WorkCaptureRejection.EmptyTitle -> "add requires a non-empty title"
                                        | WorkCaptureRejection.InvalidPriority priority ->
                                            $"invalid priority '{priority}'; use high, medium, or low"
                                        | WorkCaptureRejection.InvalidId id -> $"invalid work-item ID '{id}'"
                                        | WorkCaptureRejection.DuplicateInQueue id -> $"work item '{id}' already exists"
                                        | WorkCaptureRejection.DuplicateInContext id ->
                                            $"work item '{id}' already exists in repository context"
                                    )
                                | WorkCaptureOutcome.Planned plan ->
                                    FileBacklogQueueRepository.captureItem root plan createdByActor context.WorkItems
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok row ->
                printf "%s" (BacklogTransitionEffectContract.renderJson row)
                0
    | _ ->
        eprintfn "ERROR work capture requires valid --title and --occurred-at"
        2

/// `add TITLE ...` is production's own shorthand for `captureWork`
/// (`tools/ros_cli.mjs`), which supplies `new Date().toISOString()`
/// internally rather than requiring the caller to pass one. This CLI-shell
/// wrapper reads the real clock (a legitimate Tier 4 host effect) only when
/// the caller does not already supply `--occurred-at`, then delegates to
/// `runWorkCapture` unchanged -- no new decision logic. Unlike production,
/// trailing file-attachment arguments are not supported here (`work attach`
/// is the F# equivalent effect) and are silently ignored rather than
/// attached, matching how `runWorkCapture` already ignores any token it
/// does not recognize as one of its own flags.
let private runAdd root (arguments: string list) =
    match arguments with
    | title :: rest when not (title.StartsWith "--") ->
        let occurredAt =
            optionValue "--occurred-at" arguments
            |> Option.defaultValue (DateTimeOffset.UtcNow.ToString "yyyy-MM-ddTHH:mm:ss.fffZ")

        ProvenanceCommands.withResolvedActor rest (runWorkCapture root ("--title" :: title :: "--occurred-at" :: occurredAt :: rest))
    | _ ->
        eprintfn "ERROR add requires a title, e.g. ros add \"Title\""
        2

/// Mirrors production `updateWorkUnlocked`/`findOrCreateQueueEntry`
/// (`tools/ros_cli.mjs`) via `Ros.Domain.Work.WorkUpdate`, excluding
/// `--file` attachment. Real effect, same lock/journal as the other
/// backlog effects. `--tag`'s presence (not its value) decides whether
/// tags change at all, matching production's own `rest.includes("--tag")`
/// gate -- an update with no `--tag` flag never touches existing tags.
let private runWorkUpdate root arguments =
    let id = optionValue "--id" arguments
    let occurredAt = optionValue "--occurred-at" arguments

    match id, occurredAt with
    | Some workItemId, Some timestamp ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match readWorkContext root with
                            | Error message -> Error message
                            | Ok context ->
                                let queueContainsId =
                                    FileBacklogQueueRepository.readItems root |> List.exists (fun item -> item.Id = workItemId)

                                let contextContainsId = context.WorkItems |> List.exists (fun item -> item.Id = workItemId)

                                let request: WorkUpdateRequest =
                                    { Id = workItemId
                                      QueueContainsId = queueContainsId
                                      ContextContainsId = contextContainsId
                                      Title = optionValue "--title" arguments
                                      Description = optionValue "--description" arguments
                                      Tags = if arguments |> List.contains "--tag" then Some(optionValues "--tag" arguments) else None
                                      Priority = optionValue "--priority" arguments
                                      OccurredAt = timestamp }

                                match WorkUpdate.plan request with
                                | WorkUpdateOutcome.Rejected rejection ->
                                    Error(
                                        match rejection with
                                        | WorkUpdateRejection.InvalidId id -> $"invalid work-item ID '{id}'"
                                        | WorkUpdateRejection.NotFound id -> $"work item '{id}' was not found"
                                        | WorkUpdateRejection.EmptyTitle -> "title cannot be empty"
                                        | WorkUpdateRejection.InvalidPriority priority ->
                                            $"invalid priority '{priority}'; use high, medium, or low"
                                    )
                                | WorkUpdateOutcome.Planned plan ->
                                    FileBacklogQueueRepository.applyUpdate root workItemId plan context.WorkItems
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok row ->
                printf "%s" (BacklogTransitionEffectContract.renderJson row)
                0
    | _ ->
        eprintfn "ERROR work update requires valid --id and --occurred-at"
        2

/// Mirrors production `fileOptions`' exact linear scan and guard: each
/// `--file` occurrence must be immediately followed by a value that is not
/// itself another flag, split on the first `=` into `PATH` and an optional
/// `NAME`.
let private parseFileArguments (arguments: string list) : Result<(string * string option) list, string> =
    let rec loop remaining acc =
        match remaining with
        | "--file" :: value :: rest when not (value.StartsWith "--") ->
            let separator = value.IndexOf '='

            let entry =
                if separator > 0 then
                    value.Substring(0, separator), Some(value.Substring(separator + 1))
                else
                    value, None

            loop rest (entry :: acc)
        | "--file" :: _ -> Error "--file requires PATH or PATH=NAME"
        | _ :: rest -> loop rest acc
        | [] -> Ok(List.rev acc)

    loop arguments []

/// Mirrors production `attachFileUnlocked`/`findOrCreateQueueEntry`
/// (`tools/ros_cli.mjs`) via `Ros.Domain.Work.WorkAttachment`. Production's
/// own `work attach` calls the fully-locked `attachFile` once per file in
/// its own loop -- not once for the whole batch -- so this attaches
/// exactly one file per lock acquisition too, matching that same
/// per-file commit granularity.
let private attachOneFile root (id: string) (sourcePath: string) (nameOverride: string option) (occurredAt: string) : Result<BacklogQueueRow, string> =
    match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
    | Error failure -> Error failure.Message
    | Ok lease ->
        let result =
            try
                match WorkStateTransaction.recover root with
                | Error failure -> Error failure.Message
                | Ok() ->
                    match BacklogStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match readWorkContext root with
                        | Error message -> Error message
                        | Ok context ->
                            try
                                let resolvedPath = Path.GetFullPath(Path.Combine(root, sourcePath))
                                let fileBytes = File.ReadAllBytes resolvedPath

                                let displayName =
                                    let candidate = nameOverride |> Option.defaultValue (Path.GetFileName sourcePath)
                                    let trimmed = candidate.Trim()
                                    if trimmed = "" then "file" else trimmed

                                let queueContainsId = FileBacklogQueueRepository.readItems root |> List.exists (fun item -> item.Id = id)
                                let contextContainsId = context.WorkItems |> List.exists (fun item -> item.Id = id)

                                let request: WorkAttachmentRequest =
                                    { Id = id
                                      QueueContainsId = queueContainsId
                                      ContextContainsId = contextContainsId
                                      DisplayName = displayName
                                      Size = int64 fileBytes.Length
                                      ExistingAttachmentSequences = FileBacklogQueueRepository.readAttachmentSequences root id
                                      OccurredAt = occurredAt }

                                match WorkAttachment.plan request with
                                | WorkAttachmentOutcome.Rejected rejection ->
                                    Error(
                                        match rejection with
                                        | WorkAttachmentRejection.InvalidId id -> $"invalid work-item ID '{id}'"
                                        | WorkAttachmentRejection.NotFound id -> $"work item '{id}' was not found"
                                    )
                                | WorkAttachmentOutcome.Planned plan ->
                                    FileBacklogQueueRepository.applyAttachment root id plan fileBytes context.WorkItems
                            with error ->
                                Error error.Message
            with error ->
                lease.Release() |> ignore
                reraise ()

        match lease.Release(), result with
        | Error releaseFailure, Ok _ -> Error releaseFailure.Message
        | _, Error message -> Error message
        | Ok(), Ok row -> Ok row

let private runWorkAttach root arguments =
    let id = optionValue "--id" arguments
    let occurredAt = optionValue "--occurred-at" arguments

    match id, occurredAt, parseFileArguments arguments with
    | Some workItemId, Some timestamp, Ok((_ :: _) as files) ->
        let rec attachAll remaining =
            match remaining with
            | [ (sourcePath, nameOverride) ] -> attachOneFile root workItemId sourcePath nameOverride timestamp
            | (sourcePath, nameOverride) :: rest ->
                match attachOneFile root workItemId sourcePath nameOverride timestamp with
                | Error message -> Error message
                | Ok _ -> attachAll rest
            | [] -> Error "work attach requires at least one --file PATH[=NAME]"

        match attachAll files with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok row ->
            printf "%s" (BacklogTransitionEffectContract.renderJson row)
            0
    | _, _, Error message ->
        eprintfn "ERROR %s" message
        2
    | _ ->
        eprintfn "ERROR work attach requires valid --id, --occurred-at, and at least one --file PATH[=NAME]"
        2

let private workActionCode (action: WorkAction) =
    match action with
    | WorkAction.Begin -> "begin"
    | WorkAction.Block -> "block"
    | WorkAction.Resume -> "resume"
    | WorkAction.Complete -> "complete"

let private workStateCode (state: LiveWorkState) =
    match state with
    | LiveWorkState.Ready -> "ready"
    | LiveWorkState.Active -> "active"
    | LiveWorkState.Blocked -> "blocked"
    | LiveWorkState.Complete -> "complete"

let private workContextRejectionMessage (rejection: WorkContextRejection) =
    match rejection with
    | WorkContextRejection.NoWorkItems -> "start requires at least one work-item ID"
    | WorkContextRejection.InvalidWorkItemId id -> $"invalid work-item ID '{id}'"
    | WorkContextRejection.WorkItemNotInContext id -> $"work item '{id}' is not in repository context"
    | WorkContextRejection.ItemTransitionRejected(id, transitionRejection) ->
        match transitionRejection with
        | TransitionRejection.IllegalTransition(state, action) ->
            $"cannot {workActionCode action} '{id}' from '{workStateCode state}'"
        | TransitionRejection.BlockReasonRequired -> "block requires --reason"
        | TransitionRejection.MissingEvidence missing -> $"""completion evidence missing for '{id}': {String.Join(", ", missing)}"""

let private telemetryRejectionMessage (workItemId: string) (rejection: TelemetryResolutionRejection) =
    match rejection with
    | TelemetryResolutionRejection.DetachedConflict _ ->
        $"a detached telemetry execution must be linked before creating a new one for '{workItemId}', which this command does not yet support selecting"
    | TelemetryResolutionRejection.Ambiguous ids ->
        $"""multiple detached telemetry executions require explicit selection for '{workItemId}' ({String.Join(", ", ids)}), which this command does not yet support"""

/// Mirrors production's own `fs.existsSync` check on every provided
/// evidence path (`transitionUnlocked`'s `complete` branch): both
/// `EvidenceIssue` cases surface identical text, since Node's
/// `existsSync` swallows every underlying error (permission denied
/// included) and reports a plain missing path either way.
let private evidenceIssueMessage (issue: EvidenceIssue) =
    match issue with
    | EvidenceIssue.Missing evidence -> $"evidence path does not exist: {evidence.Path}"
    | EvidenceIssue.Unavailable(evidence, _) -> $"evidence path does not exist: {evidence.Path}"

/// Shared by every live-work transition effect (`work start`, `work
/// resume`, ...): resolves a plan's telemetry against currently observable
/// execution files, and whenever resolution halts on `PendingNewExecution`,
/// creates the missing execution (production's own `startExecution`) and
/// re-resolves against freshly re-observed candidates -- a real effect
/// boundary re-reading disk state, not a second pure pass. Bounded to one
/// creation attempt per requested work item so a persistent failure cannot
/// loop forever. `parentExecutionIdFor` supplies `CreateExecutionRequest.
/// ParentExecutionId` per work item: `work resume`'s rare
/// no-active-candidate path supplies the work item's most recently
/// created execution; every other action passes `fun _ -> None` (see
/// `CreateExecutionRequest.ParentExecutionId` for why this corrects,
/// rather than replicates, production's own behavior here).
let private resolveContextTelemetryWithCreation
    root
    (identityOverrides: IdentityInputs)
    (classifications: string list)
    (parentExecutionIdFor: string -> string option)
    attemptsLeft
    (candidatePlan: WorkContextPlan)
    =
    let rec resolve attemptsLeft (candidatePlan: WorkContextPlan) =
        let telemetryRepository: TelemetryStateRepository =
            { Observe =
                fun observedWorkItemId ->
                    { LinkedExecutionIds =
                        candidatePlan.WorkItems
                        |> List.tryFind (fun item -> item.Id = observedWorkItemId)
                        |> Option.map _.TelemetryExecutionIds
                        |> Option.defaultValue []
                      Candidates = FileTelemetryStateRepository.readCandidates root observedWorkItemId
                      RequestedExecutionId = None } }

        match WorkOperations.resolveContextTelemetry telemetryRepository candidatePlan with
        | ResolvedTelemetryContextOutcome.Resolved resolvedPlan -> Ok resolvedPlan
        | ResolvedTelemetryContextOutcome.Rejected(workItemId, rejection) -> Error(telemetryRejectionMessage workItemId rejection)
        | ResolvedTelemetryContextOutcome.PendingNewExecution workItemId ->
            if attemptsLeft <= 0 then
                Error $"unable to resolve a telemetry execution for '{workItemId}'"
            else
                match candidatePlan.WorkItems |> List.tryFind (fun item -> item.Id = workItemId) with
                | None -> Error $"work item '{workItemId}' vanished during telemetry resolution"
                | Some item ->
                    let createRequest: FileTelemetryExecutionRepository.CreateExecutionRequest =
                        { WorkItemId = workItemId
                          WorkType = item.WorkType
                          Classifications = classifications
                          ClassificationRationale = None
                          ExecutionId = None
                          IdentityOverrides = { identityOverrides with ParentExecutionId = parentExecutionIdFor workItemId } }

                    match FileTelemetryExecutionRepository.createExecution root createRequest with
                    | Error message -> Error message
                    | Ok _ -> resolve (attemptsLeft - 1) candidatePlan

    resolve attemptsLeft candidatePlan

/// Same `{workItems, events}` shape production's own `work start`/`begin`/
/// `resume`/`complete`/`done` CLI handlers print: the freshly written
/// `workItems` array verbatim (unmodeled fields included) and the eventId
/// of every event this invocation produced.
let private renderWorkTransitionOutput (writtenItems: JsonArray) (eventIds: string list) =
    let output = JsonObject()
    output["workItems"] <- writtenItems.DeepClone()
    let eventsNode = JsonArray()
    eventIds |> List.iter (fun id -> eventsNode.Add(JsonValue.Create id: JsonNode))
    output["events"] <- eventsNode
    output.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2))

/// Mirrors production `startWork`/`transitionUnlocked` (`tools/ros_cli.mjs`)
/// for the `begin` action only -- the real effect behind `./ros work
/// start`. Writes `.ros/context/current.json` and appends `.ros/events/
/// events.jsonl` (`FileWorkContextRepository`) under the shared
/// `work-protocol` lock and `work-state` recovery journal (MIG-05),
/// creating a new telemetry execution record
/// (`FileTelemetryExecutionRepository`, production's own `startExecution`)
/// whenever the frozen decision layer's `TelemetryPlanResolution` halts on
/// `PendingNewExecution`, then re-resolving. Deliberately excludes
/// `resume`/`block`/`complete` (a separate, later increment), explicit
/// `--identity-*`/`--execution-id`/`--conclusion` overrides (identity is
/// discovered purely from the environment), and
/// `recordTelemetryLifecycle`'s within-execution "blocked"/"resumed" event
/// bookkeeping (not reachable from `begin`).
let private runWorkStart root arguments (eventActor: Actor) =
    let ids = optionValues "--id" arguments
    let occurredAt = optionValue "--occurred-at" arguments

    match ids, occurredAt with
    | (_ :: _), Some timestamp ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            let queueItems = FileBacklogQueueRepository.readItems root

                            let guard =
                                ids
                                |> List.tryPick (fun id ->
                                    match queueItems |> List.tryFind (fun item -> item.Id = id) with
                                    | Some item when item.Status <> "ready" ->
                                        Some(
                                            if item.Status = "abandoned" then
                                                $"cannot start backlog item '{id}': it was abandoned"
                                            else
                                                $"cannot start backlog item '{id}' from '{item.Status}'; mark it ready first"
                                        )
                                    | _ -> None)

                            match guard with
                            | Some message -> Error message
                            | None ->
                                match readWorkContext root with
                                | Error message -> Error message
                                | Ok context ->
                                    let repositoryId = FileWorkConfigRepository.readRepositoryId root

                                    let observedGitPathsResult =
                                        if context.StartedAt.IsNone then realObservedGitPaths root else Ok []

                                    match observedGitPathsResult with
                                    | Error failure -> Error(formatGitFailure failure)
                                    | Ok observedGitPaths ->
                                        let actor =
                                            optionValue "--actor" arguments
                                            |> Option.orElse (Environment.GetEnvironmentVariable "ROS_ACTOR" |> Option.ofObj)
                                            |> Option.orElse (FileWorkContextRepository.readExistingActor root)
                                            |> Option.defaultValue "unknown"

                                        let classifications = optionValues "--classification" arguments

                                        let request: WorkContextPlanRequest =
                                            { Context = context
                                              Action = WorkAction.Begin
                                              WorkItemIds = ids
                                              NewItemType = optionValue "--type" arguments |> Option.defaultValue "task"
                                              TargetLocalState = defaultLocalState WorkAction.Begin
                                              BlockReason = None
                                              DefaultRequiredEvidence = Set.empty
                                              RequiredEvidenceByType = Map.empty
                                              ProvidedEvidence = []
                                              Repository = repositoryId
                                              ProtocolVersion = FileWorkConfigRepository.readProtocolVersion root
                                              Actor = actor
                                              OccurredAt = timestamp
                                              MeaningfulChangedPaths = []
                                              ObservedGitPaths = observedGitPaths
                                              TelemetryEnabled = FileWorkConfigRepository.readTelemetryEnabled root }

                                        match WorkContextPlanning.plan request with
                                        | WorkContextPlanOutcome.Rejected rejection -> Error(workContextRejectionMessage rejection)
                                        | WorkContextPlanOutcome.Planned plan ->
                                            match resolveContextTelemetryWithCreation root (ProvenanceCommands.identityOverridesFrom arguments) classifications (fun _ -> None) (ids.Length + 1) plan with
                                            | Error message -> Error message
                                            | Ok resolvedPlan -> FileWorkContextRepository.applyContextPlan root repositoryId eventActor resolvedPlan
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok(writtenItems, eventIds) ->
                printf "%s" (renderWorkTransitionOutput writtenItems eventIds)
                0
    | [], _ ->
        eprintfn "ERROR start requires at least one work-item ID"
        2
    | _, None ->
        eprintfn "ERROR work start requires valid --id and --occurred-at"
        2

/// Mirrors production `transition(root, "resume", ids, options)`
/// (`tools/ros_cli.mjs`) -- the effect behind `./ros work resume`. Live-work
/// only: unlike `begin`, `resume` never creates a new context item (an id
/// absent from context is rejected) and never observes Git. Reuses `work
/// start`'s effect infrastructure directly (`WorkContextPlanning.plan`,
/// `resolveContextTelemetryWithCreation`, `FileWorkContextRepository.
/// applyContextPlan`). Records `recordTelemetryLifecycle`'s "resumed"
/// bookkeeping (`FileTelemetryFinalizationRepository.recordLifecycle`) on
/// every currently-active execution BEFORE telemetry resolution runs,
/// matching production's own ordering: a brand-new execution `resume`
/// itself creates (when none was active) never receives this "resumed"
/// event, since it did not exist yet when this ran. That rare new-execution
/// path links `parentExecutionId` to the work item's most recently created
/// execution (`FileTelemetryQueryRepository.readLatestExecutionId`),
/// corrected here rather than reproduced as-is: production computes this
/// same value (`prior?.executionId ?? null`) but its own
/// `discoverIdentity(options.identity ?? options)` call discards it, since
/// `options.identity` is always a truthy object (even with every field
/// `undefined`) that wins the `??` over the sibling `options` object
/// `parentExecutionId` was actually set on -- confirmed against real Node,
/// which always writes `null` here. Node is being deprecated rather than
/// patched for this, so this port implements the evidently-intended
/// behavior instead of the bug. Deliberately still excludes explicit
/// `--identity-*`/`--execution-id` overrides (no CLI exposes them for
/// `resume`).
let private runWorkResume root arguments (eventActor: Actor) =
    let ids = optionValues "--id" arguments
    let occurredAt = optionValue "--occurred-at" arguments

    match ids, occurredAt with
    | (_ :: _), Some timestamp ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match readWorkContext root with
                            | Error message -> Error message
                            | Ok context ->
                                let repositoryId = FileWorkConfigRepository.readRepositoryId root

                                let actor =
                                    optionValue "--actor" arguments
                                    |> Option.orElse (Environment.GetEnvironmentVariable "ROS_ACTOR" |> Option.ofObj)
                                    |> Option.orElse (FileWorkContextRepository.readExistingActor root)
                                    |> Option.defaultValue "unknown"

                                let request: WorkContextPlanRequest =
                                    { Context = context
                                      Action = WorkAction.Resume
                                      WorkItemIds = ids
                                      NewItemType = "task"
                                      TargetLocalState = defaultLocalState WorkAction.Resume
                                      BlockReason = None
                                      DefaultRequiredEvidence = Set.empty
                                      RequiredEvidenceByType = Map.empty
                                      ProvidedEvidence = []
                                      Repository = repositoryId
                                      ProtocolVersion = FileWorkConfigRepository.readProtocolVersion root
                                      Actor = actor
                                      OccurredAt = timestamp
                                      MeaningfulChangedPaths = []
                                      ObservedGitPaths = []
                                      TelemetryEnabled = FileWorkConfigRepository.readTelemetryEnabled root }

                                match WorkContextPlanning.plan request with
                                | WorkContextPlanOutcome.Rejected rejection -> Error(workContextRejectionMessage rejection)
                                | WorkContextPlanOutcome.Planned plan ->
                                    let lifecycleResult =
                                        if request.TelemetryEnabled then
                                            ids
                                            |> List.fold
                                                (fun acc id ->
                                                    match acc with
                                                    | Error _ -> acc
                                                    | Ok() -> FileTelemetryFinalizationRepository.recordLifecycle root id "resumed" timestamp None)
                                                (Ok())
                                        else
                                            Ok()

                                    match lifecycleResult with
                                    | Error message -> Error message
                                    | Ok() ->
                                        match
                                            resolveContextTelemetryWithCreation
                                                root
                                                (ProvenanceCommands.identityOverridesFrom arguments)
                                                []
                                                (FileTelemetryQueryRepository.readLatestExecutionId root)
                                                (ids.Length + 1)
                                                plan
                                        with
                                        | Error message -> Error message
                                        | Ok resolvedPlan -> FileWorkContextRepository.applyContextPlan root repositoryId eventActor resolvedPlan
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok(writtenItems, eventIds) ->
                printf "%s" (renderWorkTransitionOutput writtenItems eventIds)
                0
    | [], _ ->
        eprintfn "ERROR resume requires at least one work-item ID"
        2
    | _, None ->
        eprintfn "ERROR work resume requires valid --id and --occurred-at"
        2

/// Mirrors production `transition(root, "complete", ids, options)`
/// (`tools/ros_cli.mjs`) -- the effect behind `./ros work complete`.
/// Live-work only, like `resume`: an id absent from context is rejected.
/// Git is always observed (production's own `observedGitPaths` gate is
/// `action === "complete" || ...`), and required completion evidence
/// comes from `ros.json`'s `workProtocol.completionEvidence`
/// (`FileWorkConfigRepository.readCompletionEvidence`), verified against
/// the real filesystem via `WorkOperations.planVerifiedContext`/
/// `FileEvidenceRepository` -- matching production's own `fs.existsSync`
/// check on every provided evidence path, not just the required evidence
/// *types* the frozen decision layer already rejects on. When telemetry
/// is enabled, finalizes every currently active telemetry execution for
/// each completing id (`FileTelemetryFinalizationRepository.
/// finalizeWorkExecutions`, production's own unconditional
/// `finalizeWorkExecutions` call) before committing -- the shared
/// `resolveContextTelemetryWithCreation`/`TelemetryPlanResolution`
/// pipeline discards the `FinalizeExecutions` intent signal, so this is
/// called directly rather than threaded through it. A research-type
/// item's `--conclusion` (defaulting to `"inconclusive"`, matching
/// production) is written via `FileWorkContextRepository.
/// applyContextPlanWithConclusions`. Deliberately excludes
/// `options.input`/adapter-ingestion (unreachable from any CLI path) and
/// explicit `--identity-*`/`--execution-id` overrides, matching every
/// prior increment.
let private runWorkComplete root arguments (eventActor: Actor) =
    let ids = optionValues "--id" arguments
    let occurredAt = optionValue "--occurred-at" arguments
    let providedEvidence = optionValues "--evidence" arguments |> List.map parseEvidence

    match ids, occurredAt with
    | (_ :: _), Some timestamp when providedEvidence |> List.forall Option.isSome ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match readWorkContext root with
                            | Error message -> Error message
                            | Ok context ->
                                let repositoryId = FileWorkConfigRepository.readRepositoryId root

                                match realObservedGitPaths root with
                                | Error failure -> Error(formatGitFailure failure)
                                | Ok observedGitPaths ->
                                    let meaningfulChangedPaths =
                                        PathFilter.meaningfulPaths (FileWorkConfigRepository.readPathFilterConfig root) observedGitPaths

                                    let actor =
                                        optionValue "--actor" arguments
                                        |> Option.orElse (Environment.GetEnvironmentVariable "ROS_ACTOR" |> Option.ofObj)
                                        |> Option.orElse (FileWorkContextRepository.readExistingActor root)
                                        |> Option.defaultValue "unknown"

                                    let defaultRequiredEvidence, requiredEvidenceByType =
                                        FileWorkConfigRepository.readCompletionEvidence root

                                    let telemetryEnabled = FileWorkConfigRepository.readTelemetryEnabled root

                                    let request: WorkContextPlanRequest =
                                        { Context = context
                                          Action = WorkAction.Complete
                                          WorkItemIds = ids
                                          NewItemType = "task"
                                          TargetLocalState = defaultLocalState WorkAction.Complete
                                          BlockReason = None
                                          DefaultRequiredEvidence = defaultRequiredEvidence
                                          RequiredEvidenceByType = requiredEvidenceByType
                                          ProvidedEvidence = providedEvidence |> List.choose id
                                          Repository = repositoryId
                                          ProtocolVersion = FileWorkConfigRepository.readProtocolVersion root
                                          Actor = actor
                                          OccurredAt = timestamp
                                          MeaningfulChangedPaths = meaningfulChangedPaths
                                          ObservedGitPaths = observedGitPaths
                                          TelemetryEnabled = telemetryEnabled }

                                    match WorkOperations.planVerifiedContext (FileEvidenceRepository.create root) request with
                                    | VerifiedWorkContextPlanOutcome.ContextRejected rejection -> Error(workContextRejectionMessage rejection)
                                    | VerifiedWorkContextPlanOutcome.EvidenceRejected issues ->
                                        match issues with
                                        | issue :: _ -> Error(evidenceIssueMessage issue)
                                        | [] -> Error "evidence rejected"
                                    | VerifiedWorkContextPlanOutcome.Planned plan ->
                                        match resolveContextTelemetryWithCreation root (ProvenanceCommands.identityOverridesFrom arguments) [] (fun _ -> None) (ids.Length + 1) plan with
                                        | Error message -> Error message
                                        | Ok resolvedPlan ->
                                            let finalizeResult =
                                                if telemetryEnabled then
                                                    ids
                                                    |> List.fold
                                                        (fun acc workItemId ->
                                                            match acc with
                                                            | Error _ -> acc
                                                            | Ok() -> FileTelemetryFinalizationRepository.finalizeWorkExecutions root workItemId)
                                                        (Ok())
                                                else
                                                    Ok()

                                            match finalizeResult with
                                            | Error message -> Error message
                                            | Ok() ->
                                                let conclusion =
                                                    optionValue "--conclusion" arguments |> Option.defaultValue "inconclusive"

                                                let conclusions =
                                                    resolvedPlan.ItemPlans
                                                    |> List.filter (fun itemPlan -> itemPlan.Item.WorkType = "research")
                                                    |> List.map (fun itemPlan -> itemPlan.Item.Id, conclusion)
                                                    |> Map.ofList

                                                match
                                                    FileWorkContextRepository.applyContextPlanWithConclusions
                                                        root
                                                        repositoryId
                                                        conclusions
                                                        eventActor
                                                        resolvedPlan
                                                with
                                                | Error message -> Error message
                                                | Ok(writtenItems, eventIds) ->
                                                    match readWorkContext root with
                                                    | Error message -> Error message
                                                    | Ok updatedContext ->
                                                        let backlogSync =
                                                            ids
                                                            |> List.fold
                                                                (fun acc workItemId ->
                                                                    match acc with
                                                                    | Error _ -> acc
                                                                    | Ok() ->
                                                                        FileBacklogQueueRepository.markComplete
                                                                            root
                                                                            workItemId
                                                                            timestamp
                                                                            updatedContext.WorkItems)
                                                                (Ok())

                                                        match backlogSync with
                                                        | Error message -> Error message
                                                        | Ok() -> Ok(writtenItems, eventIds)
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok(writtenItems, eventIds) ->
                printf "%s" (renderWorkTransitionOutput writtenItems eventIds)
                0
    | [], _ ->
        eprintfn "ERROR complete requires at least one work-item ID"
        2
    | _ ->
        eprintfn "ERROR work complete requires valid --id, --occurred-at, and TYPE=PATH evidence"
        2

let private readRawQueueItem root (id: string) : JsonObject option =
    let path = Path.Combine(root, ".ros", "work", "queue.json")

    if not (File.Exists path) then
        None
    else
        match JsonNode.Parse(File.ReadAllText path) with
        | :? JsonObject as queue ->
            match queue["items"] with
            | :? JsonArray as items ->
                items
                |> Seq.tryPick (fun node ->
                    match node with
                    | :? JsonObject as candidate ->
                        match candidate["id"] with
                        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String && value.GetValue<string>() = id -> Some candidate
                        | _ -> None
                    | _ -> None)
            | _ -> None
        | _ -> None

/// Mirrors production `blockWork` (`tools/ros_cli.mjs`): a combined effect
/// that splits requested ids between backlog-only items (not yet started)
/// and live-context items, applying the matching real effect to each under
/// ONE held `work-protocol` lock -- production's own `blockWork` never
/// acquires the lock twice either. `--reason` is optional at the argument
/// level, matching production's own CLI: whether it is actually required
/// depends on the item's current state (`block` is illegal from anywhere
/// but `ready`/`active`, and that illegal-transition rejection fires before
/// the missing-reason one ever would), so the check is left to the same
/// decision layer every other real effect uses, not enforced eagerly here.
/// `--occurred-at` is this CLI's own synthetic determinism parameter (as
/// for every other real effect in this migration, none of which
/// production's real CLI actually exposes): production computes its own
/// timestamp per branch. For live-context ids, also records
/// `recordTelemetryLifecycle`'s "blocked" bookkeeping
/// (`FileTelemetryFinalizationRepository.recordLifecycle`) on every
/// currently-active execution before telemetry resolution runs, matching
/// production's own ordering -- backlog-only ids never reach telemetry at
/// all, since a never-started item has no execution to record against.
let private runWorkBlock root arguments (eventActor: Actor) =
    let ids = optionValues "--id" arguments
    let reason = optionValue "--reason" arguments
    let occurredAt = optionValue "--occurred-at" arguments

    match ids, occurredAt with
    | (_ :: _), Some timestamp ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match readWorkContext root with
                            | Error message -> Error message
                            | Ok context ->
                                let queueItems = FileBacklogQueueRepository.readItems root
                                let contextIdSet = context.WorkItems |> List.map (fun item -> item.Id) |> Set.ofList

                                let backlogIds =
                                    ids
                                    |> List.filter (fun id ->
                                        (queueItems |> List.exists (fun item -> item.Id = id)) && not (contextIdSet.Contains id))

                                let contextIds = ids |> List.filter (fun id -> not (List.contains id backlogIds))

                                let rec applyBacklog remaining acc =
                                    match remaining with
                                    | [] -> Ok(List.rev acc)
                                    | id :: rest ->
                                        match applyBacklogTransition root id BacklogAction.Block "block" reason timestamp context.WorkItems with
                                        | Error message -> Error message
                                        | Ok _ ->
                                            match readRawQueueItem root id with
                                            | None -> Error $"'{id}' vanished during block"
                                            | Some rawItem -> applyBacklog rest (rawItem :: acc)

                                match applyBacklog backlogIds [] with
                                | Error message -> Error message
                                | Ok backlogRows ->
                                    if contextIds.IsEmpty then
                                        Ok(backlogRows, JsonArray())
                                    else
                                        let repositoryId = FileWorkConfigRepository.readRepositoryId root

                                        let actor =
                                            optionValue "--actor" arguments
                                            |> Option.orElse (Environment.GetEnvironmentVariable "ROS_ACTOR" |> Option.ofObj)
                                            |> Option.orElse (FileWorkContextRepository.readExistingActor root)
                                            |> Option.defaultValue "unknown"

                                        let request: WorkContextPlanRequest =
                                            { Context = context
                                              Action = WorkAction.Block
                                              WorkItemIds = contextIds
                                              NewItemType = "task"
                                              TargetLocalState = defaultLocalState WorkAction.Block
                                              BlockReason = reason
                                              DefaultRequiredEvidence = Set.empty
                                              RequiredEvidenceByType = Map.empty
                                              ProvidedEvidence = []
                                              Repository = repositoryId
                                              ProtocolVersion = FileWorkConfigRepository.readProtocolVersion root
                                              Actor = actor
                                              OccurredAt = timestamp
                                              MeaningfulChangedPaths = []
                                              ObservedGitPaths = []
                                              TelemetryEnabled = FileWorkConfigRepository.readTelemetryEnabled root }

                                        match WorkContextPlanning.plan request with
                                        | WorkContextPlanOutcome.Rejected rejection -> Error(workContextRejectionMessage rejection)
                                        | WorkContextPlanOutcome.Planned plan ->
                                            let lifecycleResult =
                                                if request.TelemetryEnabled then
                                                    contextIds
                                                    |> List.fold
                                                        (fun acc id ->
                                                            match acc with
                                                            | Error _ -> acc
                                                            | Ok() -> FileTelemetryFinalizationRepository.recordLifecycle root id "blocked" timestamp reason)
                                                        (Ok())
                                                else
                                                    Ok()

                                            match lifecycleResult with
                                            | Error message -> Error message
                                            | Ok() ->
                                                match resolveContextTelemetryWithCreation root (ProvenanceCommands.identityOverridesFrom arguments) [] (fun _ -> None) (contextIds.Length + 1) plan with
                                                | Error message -> Error message
                                                | Ok resolvedPlan ->
                                                    match FileWorkContextRepository.applyContextPlan root repositoryId eventActor resolvedPlan with
                                                    | Error message -> Error message
                                                    | Ok(writtenItems, _) ->
                                                        let contextIdSet = Set.ofList contextIds

                                                        let matching = JsonArray()

                                                        for node in writtenItems do
                                                            match node with
                                                            | :? JsonObject as item ->
                                                                match item["id"] with
                                                                | :? JsonValue as value when
                                                                    value.GetValueKind() = JsonValueKind.String
                                                                    && contextIdSet.Contains(value.GetValue<string>())
                                                                    ->
                                                                    matching.Add(item.DeepClone(): JsonNode)
                                                                | _ -> ()
                                                            | _ -> ()

                                                        Ok(backlogRows, matching)
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok(backlogRows, contextItems) ->
                let combined = JsonArray()
                backlogRows |> List.iter (fun item -> combined.Add(item.DeepClone(): JsonNode))
                for item in contextItems do
                    combined.Add(item.DeepClone(): JsonNode)

                printf "%s" (combined.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
                0
    | [], _ ->
        eprintfn "ERROR block requires at least one work-item ID"
        2
    | _, None ->
        eprintfn "ERROR work block requires valid --id and --occurred-at"
        2

/// Mirrors production `queueFindings` (`tools/ros_cli.mjs`): duplicate ids,
/// invalid ids, invalid status, and invalid priority over the raw backlog
/// queue rows in `.ros/work/queue.json`.
let private runBacklogQueueValidate root arguments =
    if not (arguments |> List.forall ((=) "--json")) then
        eprintfn "%s" usage
        2
    else
        let findings = FileBacklogQueueRepository.readItems root |> BacklogQueueValidation.findings
        printf "%s" (BacklogQueueValidationContract.renderJson findings)
        if findings.IsEmpty then 0 else 1

/// Mirrors production's top-level `validate(root)` (`tools/ros_cli.mjs`):
/// the same five contributors, each already a real F# effect on its own
/// (`ArtifactPolicy.validate`, registry staleness, `WorkAttribution`,
/// `BacklogQueueValidation`, `TelemetryValidation`), combined into one
/// sorted array and rendered with production's exact `findingRecord`
/// repair-message logic. This is pure orchestration -- no contributor's
/// own decision changes here -- so it lives at the CLI composition layer
/// rather than introducing a new cross-feature Application module.
/// Sorted with ordinal string comparison over the joined `path/field/
/// message` tuple, approximating production's own locale-aware
/// `localeCompare` on the same join -- a deliberate, documented
/// simplification: every path/field/message this repository's own
/// contributors produce is plain ASCII, where ordinal and locale-aware
/// collation agree.
/// Mirrors production `validate(root)`'s own combined computation, shared
/// by the unified `validate` command and `status` (production's own
/// `statusView` calls `validate(root)` directly for its `findingCount`/
/// `nextActions` fields).
let private computeUnifiedFindings root : Result<ArtifactFinding list, string> =
    let repository = FileArtifactRepository.create root

    match ArtifactOperations.validate repository with
    | ValidationOutcome.DependencyFailure failure -> Error(dependencyFailureMessage failure)
    | ValidationOutcome.Completed artifactFindings ->
        match ArtifactOperations.checkRegistries repository with
        | RegistryCheckOutcome.DependencyFailure failure -> Error(dependencyFailureMessage failure)
        | RegistryCheckOutcome.Completed registryCombined ->
            // `checkRegistries` recomputes the same parse findings
            // `validate` already returned; keep only the staleness
            // findings from its result to avoid double-counting.
            let staleFindings = registryCombined |> List.filter (fun f -> f.Message.Contains "registry is stale")

            match computeWorkAttributionFindings root with
            | Error message -> Error message
            | Ok workFindings ->
                let convert (path: string) (field: string) (message: string) : ArtifactFinding = { Path = path; Field = field; Message = message }

                let queueFindings =
                    FileBacklogQueueRepository.readItems root |> BacklogQueueValidation.findings |> List.map (fun f -> convert f.Path f.Field f.Message)

                let telemetryFindingsConverted =
                    FileTelemetryValidationRepository.findings root |> List.map (fun f -> convert f.Path f.Field f.Message)

                let workFindingsConverted = workFindings |> List.map (fun f -> convert f.Path f.Field f.Message)

                match ProvenanceCommands.findingsOf FindingSeverity.Error root with
                | Error message -> Error message
                | Ok provenanceErrors ->
                    artifactFindings
                    @ staleFindings
                    @ workFindingsConverted
                    @ queueFindings
                    @ telemetryFindingsConverted
                    @ (provenanceErrors |> List.map ProvenanceCommands.toArtifactFinding)
                    |> List.sortWith (fun a b -> System.String.CompareOrdinal($"{a.Path}\000{a.Field}\000{a.Message}", $"{b.Path}\000{b.Field}\000{b.Message}"))
                    |> Ok

let private runValidateUnified root arguments =
    if not (arguments |> List.forall ((=) "--json")) then
        eprintfn "%s" usage
        2
    else
        match computeUnifiedFindings root, ProvenanceCommands.findingsOf FindingSeverity.Warning root with
        | Error message, _
        | _, Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok all, Ok provenanceWarnings ->
            let warnings = provenanceWarnings |> List.map ProvenanceCommands.toArtifactFinding

            if arguments |> List.contains "--json" then
                printf "%s" (FindingContract.renderJsonWithWarnings all warnings)
                if all.IsEmpty then 0 else 1
            else
                for warning in warnings do
                    eprintfn "WARN %s\n  REPAIR %s" (renderFinding warning) (FindingContract.repair warning)

                if all.IsEmpty then
                    if warnings.IsEmpty then
                        printfn "validation passed"
                    else
                        printfn "validation passed with %d warning(s)" warnings.Length

                    0
                else
                    for finding in all do
                        eprintfn "ERROR %s\n  REPAIR %s" (renderFinding finding) (FindingContract.repair finding)

                    eprintfn "validation failed with %d error(s)" all.Length
                    1

/// Mirrors production `statusView` (`tools/ros_cli.mjs`): the same
/// `contextView` read `work context` uses, narrowed to six fields per
/// item, plus the unified `validate` findings' count/pass-fail summary
/// and deduplicated repair hints, plus real execution/active-execution
/// counts from `telemetry show`'s own read.
let private runStatus root packageRoot verbose =
    match computeUnifiedFindings root with
    | Error message ->
        eprintfn "ERROR %s" message
        1
    | Ok findings ->
        match FileWorkContextRepository.readContextView root None with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok contextView ->
            let executions = FileTelemetryQueryRepository.readAll root

            let statusField (node: JsonObject) : string option =
                match node["status"] with
                | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
                | _ -> None

            let executionCount = executions.Length
            let activeExecutionCount = executions |> List.filter (fun e -> statusField e = Some "active") |> List.length

            let cloneField (node: JsonObject) (name: string) : JsonNode =
                match node[name] with
                | null -> null
                | v -> v.DeepClone()

            let cloneOrEmptyArray (node: JsonObject) (name: string) : JsonNode =
                match node[name] with
                | null -> JsonArray() :> JsonNode
                | v -> v.DeepClone()

            let workItemsNode = JsonArray()

            match contextView["workItems"] with
            | :? JsonArray as items ->
                for item in items do
                    match item with
                    | :? JsonObject as obj ->
                        let projected = JsonObject()
                        projected["id"] <- cloneField obj "id"
                        projected["type"] <- cloneField obj "type"
                        projected["state"] <- cloneField obj "state"
                        projected["semanticState"] <- cloneField obj "semanticState"
                        projected["allowedActions"] <- cloneOrEmptyArray obj "allowedActions"
                        projected["telemetryExecutionIds"] <- cloneOrEmptyArray obj "telemetryExecutionIds"
                        workItemsNode.Add(projected: JsonNode)
                    | _ -> ()
            | _ -> ()

            let telemetryNode = JsonObject()
            telemetryNode["executionCount"] <- JsonValue.Create executionCount
            telemetryNode["activeExecutionCount"] <- JsonValue.Create activeExecutionCount

            let nextActionsNode = JsonArray()

            if findings.IsEmpty then
                nextActionsNode.Add(JsonValue.Create "Select an allowed work transition or begin a new work item.": JsonNode)
            else
                findings
                |> List.map FindingContract.repair
                |> List.distinct
                |> List.iter (fun repair -> nextActionsNode.Add(JsonValue.Create repair: JsonNode))

            let output = JsonObject()
            output["repository"] <- JsonValue.Create(FileWorkConfigRepository.readRepositoryId root)
            output["protocolVersion"] <- JsonValue.Create(FileWorkConfigRepository.readProtocolVersion root)
            output["validation"] <- JsonValue.Create(if findings.IsEmpty then "passed" else "failed")
            output["findingCount"] <- JsonValue.Create findings.Length
            output["workItems"] <- workItemsNode
            output["telemetry"] <- telemetryNode
            output["nextActions"] <- nextActionsNode

            // Additive: every key above predates the lifecycle interface and
            // is unchanged. `installation` is new, so an existing consumer
            // that reads only the keys it already knows is unaffected.
            match Lifecycle.installationNode root packageRoot with
            | Some(node: JsonNode) ->
                if not verbose then
                    match node with
                    | :? JsonObject as installation -> installation.Remove "managedArtifacts" |> ignore
                    | _ -> ()

                output["installation"] <- node
            | None -> ()

            printf "%s" (output.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
            0

/// Mirrors production `telemetryFindings` (`tools/ros_telemetry.mjs`), the
/// telemetry contributor to `validate`'s combined findings array.
let private runTelemetryValidate root arguments =
    if not (arguments |> List.forall ((=) "--json")) then
        eprintfn "%s" usage
        2
    else
        let findings = FileTelemetryValidationRepository.findings root
        printf "%s" (TelemetryValidationContract.renderJson findings)
        if findings.IsEmpty then 0 else 1

/// Mirrors production `telemetry adapters` (`tools/ros_telemetry.mjs`):
/// prints the static provider-adapter catalog verbatim. No file I/O, no
/// lock -- ingestion itself (mapping each adapter's payload shape into the
/// normalized schema) is a separately-scoped later slice.
let private runTelemetryAdapters () =
    let node = JsonArray()
    TelemetryAdapters.all |> List.iter (fun name -> node.Add(JsonValue.Create name: JsonNode))
    printf "%s" (node.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
    0

/// Mirrors production `telemetry show [TARGET]` (`showTelemetry`,
/// `tools/ros_telemetry.mjs`) -- Phase A/MIG-08's first increment, and the
/// first telemetry-producer command with real F# parity. `TARGET` is
/// positional, not a flag, matching production's own `telemetryTarget(args)`
/// (`args[2]`, undefined when it starts with `--`). No target prints every
/// execution record; an `EXE-`-prefixed target resolves exactly one record
/// or production's exact rejection message; any other target filters by
/// work-item ID, where an empty result is not a rejection (production's own
/// `showTelemetry` never throws for this branch). Read-only: no lock, no
/// write, and no new identity/Git/capability machinery -- it only reads the
/// same `.ros/telemetry/executions/*.json` records `work start`/`work
/// complete` already produce.
let private runTelemetryShow root (arguments: string list) =
    let target =
        arguments
        |> List.tryHead
        |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))

    let renderRecords (records: JsonObject list) =
        let node = JsonArray()
        records |> List.iter (fun record -> node.Add(record.DeepClone(): JsonNode))
        printf "%s" (node.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
        0

    match target with
    | None -> renderRecords (FileTelemetryQueryRepository.readAll root)
    | Some value when value.StartsWith("EXE-", StringComparison.Ordinal) ->
        match FileTelemetryQueryRepository.readByExecutionId root value with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok record ->
            printf "%s" (record.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
            0
    | Some workItemId -> renderRecords (FileTelemetryQueryRepository.readByWorkItemId root workItemId)

/// Mirrors production `telemetry summary`/`telemetry summarize [TARGET]`
/// (`summarizeTelemetry`, `tools/ros_telemetry.mjs`) -- MIG-08's third
/// increment, and the largest read-only telemetry command: four real
/// aggregation strategies (`sum`/`maximum`/`latest-per-session`/`none`,
/// falling back to `latest`) plus an interval-sweep timing summary, over
/// every metric recorded across matching execution files. `TARGET` is
/// positional like `telemetry show`, but here it is used purely as a
/// work-item-id filter -- unlike `show`, `summary` never special-cases an
/// `EXE-`-prefixed value, matching production's own `!workItemId ||
/// record.workItemId === workItemId` check exactly (passing an execution id
/// here silently matches nothing, reproducing that real quirk rather than
/// improving on it). Read-only: no lock, no write.
let private runTelemetrySummary root (arguments: string list) =
    let workItemId =
        arguments
        |> List.tryHead
        |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))

    let executions = FileTelemetryQueryRepository.readSummaryExecutions root workItemId
    let summary = TelemetrySummary.summarize workItemId executions

    let output = JsonObject()
    output["schemaVersion"] <- JsonValue.Create summary.SchemaVersion

    output["workItemId"] <-
        match summary.WorkItemId with
        | Some id -> JsonValue.Create id
        | None -> null

    output["executionCount"] <- JsonValue.Create summary.ExecutionCount
    let providersNode = JsonArray()
    summary.Providers |> List.iter (fun provider -> providersNode.Add(JsonValue.Create provider: JsonNode))
    output["providers"] <- providersNode
    let runtimesNode = JsonArray()
    summary.Runtimes |> List.iter (fun runtime -> runtimesNode.Add(JsonValue.Create runtime: JsonNode))
    output["runtimes"] <- runtimesNode

    let timing = summary.Timing
    let timingNode = JsonObject()
    timingNode["fullyFinalized"] <- JsonValue.Create timing.FullyFinalized
    timingNode["finalizedExecutionCount"] <- JsonValue.Create timing.FinalizedExecutionCount
    timingNode["activeExecutionCount"] <- JsonValue.Create timing.ActiveExecutionCount

    timingNode["earliestStartedAt"] <-
        match timing.EarliestStartedAt with
        | Some value -> JsonValue.Create value
        | None -> null

    timingNode["latestFinalizedAt"] <-
        match timing.LatestFinalizedAt with
        | Some value -> JsonValue.Create value
        | None -> null

    timingNode["calendarSpanMs"] <-
        match timing.CalendarSpanMs with
        | Some value -> JsonValue.Create value
        | None -> null

    timingNode["totalExecutionWallMs"] <-
        match timing.TotalExecutionWallMs with
        | Some value -> JsonValue.Create value
        | None -> null

    timingNode["overlappingExecutionMs"] <-
        match timing.OverlappingExecutionMs with
        | Some value -> JsonValue.Create value
        | None -> null

    output["timing"] <- timingNode

    let metricsNode = JsonArray()

    summary.Metrics
    |> List.iter (fun metric ->
        let metricNode = JsonObject()
        metricNode["id"] <- JsonValue.Create metric.Id

        metricNode["value"] <-
            match metric.Value with
            | Some value -> JsonValue.Create value
            | None -> null

        metricNode["unit"] <- JsonValue.Create metric.Unit

        metricNode["currency"] <-
            match metric.Currency with
            | Some currency -> JsonValue.Create currency
            | None -> null

        metricNode["dimensions"] <-
            match JsonNode.Parse metric.DimensionsKey with
            | :? JsonObject as dimensions -> dimensions
            | _ -> JsonObject()

        metricNode["aggregation"] <- JsonValue.Create metric.Aggregation
        metricNode["measurements"] <- JsonValue.Create metric.Measurements

        metricNode["note"] <-
            match metric.Note with
            | Some note -> JsonValue.Create note
            | None -> null

        metricsNode.Add(metricNode: JsonNode))

    output["metrics"] <- metricsNode

    printf "%s" (output.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
    0

/// Mirrors production `telemetry finalize [TARGET]` (`finalizeExecution`,
/// `tools/ros_telemetry.mjs`) -- MIG-08's fourth increment, and the first
/// write-path telemetry-producer command. `TARGET` is positional like
/// `telemetry show`/`telemetry summary`, but resolves differently from
/// both: an `EXE-`-prefixed value matches by exact execution id; any other
/// value matches by work-item id regardless of the matching execution's own
/// status (unlike `show`'s work-item branch, an already-finalized match is
/// legal here too); no target at all requires exactly one currently
/// active-or-blocked work item, rejecting production's exact ambiguity
/// message otherwise. An already-finalized resolved execution is returned
/// untouched, with no lock taken at all (matching production's own pre-lock
/// fast return); otherwise it is finalized via the same real effect `work
/// complete` already uses. Deliberately excludes `--input`/adapter-ingestion
/// -- the one place it is reachable in production at all -- a separately
/// scoped later slice; passing `--input` is rejected outright rather than
/// silently ignored.
let private runTelemetryFinalize root (arguments: string list) =
    if arguments |> List.contains "--input" then
        eprintfn "ERROR telemetry finalize --input is not yet supported by this CLI"
        2
    else
        let target = arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))

        match FileTelemetryFinalizationRepository.finalizeTarget root target with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok record ->
            if not (arguments |> List.contains "--quiet") then
                printf "%s" (record.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))

            0

/// Mirrors production `telemetry record [TARGET] --metric ID --value VALUE`
/// (`recordTelemetryMetric`/`normalizeMetric`/`addMetric`,
/// `tools/ros_telemetry.mjs`) -- MIG-08's fifth increment, and the second
/// write-path telemetry-producer command. Unlike `finalize`, target
/// resolution is `activeOnly`: an execution that is not currently
/// `"active"` (including one finalized between resolution and the lock
/// being acquired, matching production's own re-resolve-under-the-lock
/// race check) is rejected with production's exact `"... was not found or
/// is already finalized"` message. `--confidence` mirrors production's own
/// permissive parsing: a value that parses as a finite number is stored
/// numerically, any other text is stored as-is, and omitting the flag
/// entirely stores `null`. `--quality`/`--scope`/`--source-*` all default
/// exactly as production's CLI does; `--unit`/`--currency`/`--collected-at`
/// are optional overrides of the metric registry's own values. `--value`'s
/// parse failure is deliberately not surfaced here -- it becomes `NaN` and
/// is passed through unvalidated, so the finite-value rejection happens at
/// the same point production's does, after target resolution and lock
/// acquisition, not before.
let private runTelemetryRecord root (arguments: string list) =
    let target = arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))

    match optionValue "--metric" arguments, optionValue "--value" arguments with
    | None, _
    | _, None ->
        eprintfn "ERROR telemetry record requires --metric and --value"
        1
    | Some metricId, Some rawValue ->
        let value =
            match Double.TryParse(rawValue, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, parsed -> parsed
            | false, _ -> Double.NaN

        let confidence =
            match optionValue "--confidence" arguments with
            | None -> FileTelemetryFinalizationRepository.NoConfidence
            | Some text ->
                match Double.TryParse(text, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                | true, parsed when Double.IsFinite parsed -> FileTelemetryFinalizationRepository.NumericConfidence parsed
                | _ -> FileTelemetryFinalizationRepository.TextConfidence text

        let request: FileTelemetryFinalizationRepository.RecordMetricRequest =
            { MetricId = metricId
              Value = value
              Unit = optionValue "--unit" arguments
              Currency = optionValue "--currency" arguments
              Quality = optionValue "--quality" arguments |> Option.defaultValue "observed"
              Confidence = confidence
              Scope = optionValue "--scope" arguments |> Option.defaultValue "execution"
              Source =
                { Type = optionValue "--source-type" arguments |> Option.defaultValue "agent-report"
                  Name = optionValue "--source-name" arguments |> Option.defaultValue "ros-telemetry-cli"
                  Mechanism = optionValue "--mechanism" arguments |> Option.defaultValue "explicit-metric-record" }
              PricingSource = optionValue "--pricing-source" arguments
              PricingVersion = optionValue "--pricing-version" arguments
              CollectedAt = optionValue "--collected-at" arguments }

        match FileTelemetryFinalizationRepository.recordMetric root target request with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok record ->
            if not (arguments |> List.contains "--quiet") then
                let lastMetric =
                    match record["metrics"] with
                    | :? JsonArray as metrics when metrics.Count > 0 -> Some metrics.[metrics.Count - 1]
                    | _ -> None

                match lastMetric with
                | Some metric -> printf "%s" (metric.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
                | None -> ()

            0

/// Mirrors production `telemetry ingest [TARGET] --input FILE [--adapter
/// NAME]` (`ingestTelemetry`/`adaptInput`/`ingestAdapted`,
/// `tools/ros_telemetry.mjs`) -- MIG-08's sixth increment shipped the
/// `generic` adapter (the one with zero provider-specific field mapping);
/// a later increment added `openai-codex`, the first real provider-specific
/// field mapping ported; a further increment added the three hook adapters
/// (`anthropic-claude-hook`/`google-gemini-hook`/`github-copilot-hook`),
/// sharing one `adaptHook` parameterized only by identity provider/runtime;
/// a further increment added `anthropic-claude-statusline`
/// (`adaptClaudeStatusline`), a single-snapshot adapter (not an event
/// stream) whose one real quirk is that a present `cost.session_cumulative`
/// value gets an `"estimated"` capability status rather than
/// `"supported-observed"`; the final increment added the OTel adapter
/// family (`anthropic-claude-otel`/`google-gemini-otel`/
/// `github-copilot-otel`/`otel-json`), sharing one `adaptOtel` that looks
/// every field up across a per-record set of nested candidates (the
/// record itself, `attributes`, `resource.attributes`, `body`,
/// `dataPoint.attributes`) rather than any fixed schema. Every adapter
/// name production itself recognizes now maps to a real effect; a name
/// production itself would not recognize gets production's own exact
/// error (exit 1).
/// `--input`'s file (or `-` for stdin) is read and byte-checked here,
/// matching production's own `readTelemetryInput`; everything past that
/// (adaptation, target resolution, the mutation itself) is
/// `FileTelemetryFinalizationRepository.ingestTarget`.
let private runTelemetryIngest root (arguments: string list) =
    let target = arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))
    let adapter = optionValue "--adapter" arguments |> Option.defaultValue "generic"

    match optionValue "--input" arguments with
    | None ->
        eprintfn "ERROR --input requires a JSON or JSON Lines file; use '-' for stdin"
        1
    | Some inputPath ->
        try
            let raw = if inputPath = "-" then Console.In.ReadToEnd() else File.ReadAllText(Path.Combine(root, inputPath))
            let maxBytes = FileWorkConfigRepository.readTelemetryMaxRawPayloadBytes root

            if Text.Encoding.UTF8.GetByteCount raw > maxBytes then
                eprintfn "ERROR telemetry input exceeds %d bytes" maxBytes
                1
            else
                match FileTelemetryFinalizationRepository.ingestTarget root target adapter raw with
                | Error message ->
                    eprintfn "ERROR %s" message
                    if message.EndsWith("is not yet supported by this CLI", StringComparison.Ordinal) then 2 else 1
                | Ok record ->
                    if not (arguments |> List.contains "--quiet") then
                        printf "%s" (record.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))

                    0
        with :? IOException as error ->
            eprintfn "ERROR %s" error.Message
            1

/// Mirrors production `telemetry classify --classification NAME [...]
/// [--rationale TEXT] [--evidence-link LINK]* [--rd-context FILE]`
/// (`tools/ros_cli.mjs`) -- MIG-08's seventh increment, a thin wrapper over
/// the same generic-adapter ingest `telemetry ingest` already reuses: a
/// synthetic ingest whose `snapshotId` is `classification-{now-ms}` (a
/// real-clock timestamp, not a content digest, so a repeated call is its
/// own new event rather than deduplicated) and whose only real content is
/// the constructed `classification` object plus an explicitly empty
/// `raw: {}`. `--rd-context`'s file (or `-` for stdin) is read and parsed
/// exactly like `telemetry ingest`'s own `--input` (JSON or JSON Lines,
/// byte-checked against the same configured limit); checking for at least
/// one `--classification` happens before that read, matching production's
/// own check order exactly.
let private runTelemetryClassify root (arguments: string list) =
    let target = arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))
    let classifications = optionValues "--classification" arguments

    if classifications.IsEmpty then
        eprintfn "ERROR telemetry classify requires at least one --classification"
        1
    else
        let rationale = optionValue "--rationale" arguments
        let evidenceLinks = optionValues "--evidence-link" arguments

        let rdResult =
            match optionValue "--rd-context" arguments with
            | None -> Ok None
            | Some rdPath ->
                try
                    let raw = if rdPath = "-" then Console.In.ReadToEnd() else File.ReadAllText(Path.Combine(root, rdPath))
                    let maxBytes = FileWorkConfigRepository.readTelemetryMaxRawPayloadBytes root

                    if Text.Encoding.UTF8.GetByteCount raw > maxBytes then
                        Error $"telemetry input exceeds {maxBytes} bytes"
                    else
                        FileTelemetryFinalizationRepository.parseIngestInput raw |> Result.map Some
                with :? IOException as error ->
                    Error error.Message

        match rdResult with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok rd ->
            match FileTelemetryFinalizationRepository.classifyTarget root target classifications rationale evidenceLinks rd with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok classification ->
                if not (arguments |> List.contains "--quiet") then
                    printf "%s" (classification.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))

                0

/// Mirrors production `telemetry start WORKITEMID [--execution-id ID]
/// [--classification NAME]* [--classification-rationale TEXT]
/// [--provider NAME] [--model NAME] [--model-version VERSION]
/// [--runtime NAME] [--runtime-version VERSION] [--session ID]
/// [--conversation ID] [--run ID] [--agent ID] [--subagent ID]
/// [--parent-execution ID] [--quiet]` (`tools/ros_cli.mjs`) -- MIG-08's
/// eighth increment shipped everything but `--execution-id` and the 11
/// identity-override flags; this increment closes that gap, the last
/// piece of MIG-08's own scope. `--execution-id` selects among detached
/// (unlinked, active) candidates exactly like `resolveOrCreateExecution`'s
/// pure decision (`Ros.Domain.Telemetry.ExecutionLinkRecovery.decide`)
/// already handled -- a match recovers that execution, a non-match with
/// other candidates present rejects with production's exact `; rerun with
/// --execution-id ...` message, and no candidates at all lets the
/// requested id become the newly created execution's own id. The 11
/// identity flags parallel production's own `telemetryIdentityOptions`
/// exactly and are merged over environment-discovered identity inside
/// `createExecution`, an explicit override always winning. Prints the
/// literal JSON `null` when telemetry is disabled and no candidate
/// execution exists, matching production's own
/// `console.log(JSON.stringify(null, null, 2))`.
let private runTelemetryStart root (arguments: string list) =
    match arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal))) with
    | None ->
        eprintfn "ERROR telemetry start requires a work-item ID"
        1
    | Some workItemId ->
        let classifications = optionValues "--classification" arguments
        let classificationRationale = optionValue "--classification-rationale" arguments
        let requestedExecutionId = optionValue "--execution-id" arguments

        let identityOverrides: IdentityInputs =
            { ProvenanceCommands.identityOverridesFrom arguments with
                AgentId = optionValue "--agent" arguments
                ParentExecutionId = optionValue "--parent-execution" arguments }

        match FileTelemetryFinalizationRepository.startTarget root workItemId classifications classificationRationale requestedExecutionId identityOverrides with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok record ->
            if not (arguments |> List.contains "--quiet") then
                match record with
                | Some record -> printf "%s" (record.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
                | None -> printf "null"

            0

/// `adapter call --store FILE --request FILE`: real effect for
/// production's file-based conformance adapter (`DF-ROS-2026-A007`,
/// `docs/work-adapter-contract.md`), exercising the same request/result
/// contract a production adapter must satisfy without choosing a
/// project-management vendor. Exit code mirrors production exactly:
/// 0 success, 1 failure (including a missing/malformed request file or a
/// missing required request field), 2 unknown.
let private runAdapterCall root (arguments: string list) =
    match optionValue "--store" arguments, optionValue "--request" arguments with
    | Some store, Some requestFile ->
        let requestPath = Path.GetFullPath(Path.Combine(root, requestFile))

        if not (File.Exists requestPath) then
            eprintfn "ERROR adapter request not found: %s" requestFile
            1
        else
            // A non-object top-level JSON value (e.g. an array) behaves the
            // same as production's own `request[field]` on such a value:
            // every required field reads as missing, so this falls through
            // to the same "adapter request is missing '...'" rejection.
            let request =
                match JsonNode.Parse(File.ReadAllText requestPath) with
                | :? JsonObject as parsed -> parsed
                | _ -> JsonObject()

            let storePath = Path.GetFullPath(Path.Combine(root, store))

            match FileAdapterRepository.call storePath request with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok result ->
                let options =
                    JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

                printfn "%s" (result.ToJsonString(options))
                FileAdapterRepository.exitCode result
    | _ ->
        eprintfn "ERROR adapter call requires --store and --request"
        1

/// `adapter publish --target FILE`: real effect for production's own
/// event republish (`tools/ros_cli.mjs`), appending every
/// `.ros/events/events.jsonl` event not already present in `target` (by
/// `eventId`) and refreshing `.ros/publications.json`'s receipts.
let private runAdapterPublish root (arguments: string list) =
    match optionValue "--target" arguments with
    | None ->
        eprintfn "ERROR adapter publish requires --target"
        1
    | Some target ->
        match FileAdapterRepository.publish root target with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok outcome ->
            printfn "published %d event(s); %d duplicate(s) skipped" outcome.Published outcome.Duplicates
            0

let private readOrdoInput root arguments =
    match optionValue "--input" arguments with
    | None -> Error "--input FILE is required"
    | Some relative ->
        let path = Path.GetFullPath(Path.Combine(root, relative))
        if not (File.Exists path) then Error $"input file not found: {relative}"
        else Ok(File.ReadAllText path)

let private reportStoreOutcome = function
    | StoreOutcome.Stored ->
        printfn "{\"status\":\"stored\"}"
        0
    | StoreOutcome.AlreadyPresent ->
        printfn "{\"status\":\"already-present\"}"
        0
    | StoreOutcome.Conflict message ->
        eprintfn "ERROR %s" message
        1

let private runOrdoIngest root arguments =
    match readOrdoInput root arguments with
    | Error message ->
        eprintfn "ERROR %s" message
        2
    | Ok raw ->
        match ObservationJson.parseResolutionObservation raw with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok observation ->
            let repository = FileObservationRepository.create root
            ObservationOperations.ingestResolution repository raw observation
            |> reportStoreOutcome

let private runOrdoAssessment root arguments =
    match readOrdoInput root arguments with
    | Error message ->
        eprintfn "ERROR %s" message
        2
    | Ok raw ->
        match ObservationJson.parseAssessment raw with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok assessment ->
            FileObservationRepository.create root
            |> fun repository -> ObservationOperations.recordAssessment repository assessment
            |> reportStoreOutcome

let private runOrdoSearchObservation root arguments =
    match readOrdoInput root arguments with
    | Error message ->
        eprintfn "ERROR %s" message
        2
    | Ok raw ->
        match ObservationJson.parseSearchObservation raw with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok observation ->
            FileObservationRepository.create root
            |> fun repository -> ObservationOperations.recordSearchObservation repository observation
            |> reportStoreOutcome

let private runOrdoEffectObservation root arguments =
    match readOrdoInput root arguments with
    | Error message ->
        eprintfn "ERROR %s" message
        2
    | Ok raw ->
        match ObservationJson.parseEffectObservation raw with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok observation ->
            FileObservationRepository.create root
            |> fun repository -> ObservationOperations.recordEffectObservation repository observation
            |> reportStoreOutcome

let private runOrdoCurrent root arguments =
    match optionValue "--resolution-id" arguments with
    | None ->
        eprintfn "ERROR ordo current requires --resolution-id; ROS never infers authority from observation recency"
        2
    | Some resolutionId ->
        let repository = FileObservationRepository.create root

        match
            ObservationOperations.effectiveCurrent
                repository
                (Some resolutionId)
                (optionValues "--superseded-resolution" arguments)
        with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok projection ->
            projection
            |> ObservationJson.renderEffectiveCurrent
            |> printf "%s"
            0

let private runOrdoHandoff root arguments =
    match optionValue "--revision" arguments, optionValue "--source" arguments with
    | Some revision, Some source ->
        let repository = FileObservationRepository.create root

        match
            ObservationOperations.handoff
                repository
                (optionValue "--resolution-id" arguments)
                (optionValues "--superseded-resolution" arguments)
                revision
                source
                (optionValues "--authority-artifact" arguments)
                (optionValues "--historical-decision" arguments)
                (optionValues "--fact" arguments)
                (optionValues "--assumption" arguments)
                (optionValues "--unknown" arguments)
                (optionValues "--obligation" arguments)
                (optionValues "--verification" arguments)
                (optionValues "--next-action" arguments)
        with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok handoff ->
            handoff
            |> ObservationJson.renderHandoff
            |> printf "%s"
            0
    | _ ->
        eprintfn "ERROR ordo handoff requires --revision and --source"
        2

/// `ros --help` is public documentation, so it carries both the lifecycle
/// commands and the repository commands this CLI has always had.
let private fullHelp topic =
    match topic with
    | Some("init" | "status" | "verify" | "upgrade" | "doctor") -> Lifecycle.helpFor topic
    // Anything else -- no topic, or one this CLI does not have a page for --
    // gets the overview, which has to name the repository commands too.
    | _ -> Lifecycle.helpFor None + "\n\nRepository commands:\n  " + usage

let private repositoryDispatch root packageRoot arguments =
    let repository = FileArtifactRepository.create root
    let gitRepository = ProcessGitRepository.create root

    match arguments with
    | [ "version" ] ->
        printfn "ros-fs %s" Version
        0
    | []
    | [ "help" ]
    | [ "--help" ]
    | [ "-h" ] ->
        printfn "%s" (fullHelp None)
        0
    | "validate" :: rest -> runValidateUnified root rest
    | [ "reconcile"; "--envelope"; envelope ] -> ReconciliationCommands.reconcile root envelope
    | [ "inbox"; "list" ] -> ReconciliationCommands.inbox root
    | "foundations" :: "verify" :: rest when rest |> List.forall ((=) "--json") ->
        Foundations.run root (rest |> List.contains "--json")
    | "status" :: rest when rest |> List.forall (fun value -> value = "--json" || value = "--verbose") ->
        runStatus root packageRoot (rest |> List.contains "--verbose")
    | "artifacts" :: "validate" :: rest when rest |> List.forall ((=) "--json") ->
        runValidation (rest |> List.contains "--json") repository
    | "registry" :: "build" :: rest when rest |> List.forall ((=) "--dry-run") ->
        runRegistryBuild (rest |> List.contains "--dry-run") repository
    | [ "registry"; "check" ] -> runRegistryCheck repository
    | "git" :: "status" :: rest when rest |> List.forall ((=) "--json") ->
        runGitStatus (rest |> List.contains "--json") gitRepository
    | "work" :: "decide" :: rest -> runWorkDecision rest
    | "work" :: "plan" :: rest -> runWorkPlan root rest
    | "work" :: "context-plan" :: rest -> runWorkContextPlan root rest
    | "work" :: "backlog-decide" :: rest -> runBacklogDecision rest
    | "work" :: "backlog-promotion-plan" :: rest -> runBacklogPromotionPlan rest
    | "work" :: "validate" :: rest -> runWorkAttributionValidate root rest
    | "work" :: "backlog-validate" :: rest -> runBacklogQueueValidate root rest
    | "telemetry" :: "validate" :: rest -> runTelemetryValidate root rest
    | "work" :: "backlog-transition" :: rest -> runBacklogTransitionEffect root rest
    | "work" :: "context" :: rest -> runWorkContext root rest
    | [ "work" ] -> runWorkList root []
    | "work" :: "list" :: rest -> runWorkList root rest
    | "work" :: "ready" :: rest ->
        match residualPositionalArgs (Set.ofList [ "--tag" ]) rest with
        | [] -> runWorkList root ("--status" :: "ready" :: rest)
        | ids ->
            eprintfn
                "ERROR 'work ready %s' with an ID is not supported here; use 'work backlog-transition --action ready --id ID --occurred-at TIMESTAMP' instead"
                (String.concat " " ids)

            2
    | "work" :: "show" :: rest -> runWorkShow root rest
    | "work" :: "capture" :: rest -> ProvenanceCommands.withResolvedActor rest (runWorkCapture root rest)
    | "add" :: rest -> runAdd root rest
    | "work" :: "update" :: rest -> runWorkUpdate root rest
    | "work" :: "attach" :: rest -> runWorkAttach root rest
    | "work" :: ("begin" | "start") :: rest -> ProvenanceCommands.withResolvedActor rest (runWorkStart root rest)
    | "work" :: "resume" :: rest -> ProvenanceCommands.withResolvedActor rest (runWorkResume root rest)
    | "work" :: "block" :: rest -> ProvenanceCommands.withResolvedActor rest (runWorkBlock root rest)
    | "work" :: ("complete" | "done") :: rest -> ProvenanceCommands.withResolvedActor rest (runWorkComplete root rest)
    | [ "telemetry"; "adapters" ] -> runTelemetryAdapters ()
    | "telemetry" :: "show" :: rest -> runTelemetryShow root rest
    | "telemetry" :: ("summary" | "summarize") :: rest -> runTelemetrySummary root rest
    | "telemetry" :: "finalize" :: rest -> runTelemetryFinalize root rest
    | "telemetry" :: "record" :: rest -> runTelemetryRecord root rest
    | "telemetry" :: "ingest" :: rest -> runTelemetryIngest root rest
    | "telemetry" :: "classify" :: rest -> runTelemetryClassify root rest
    | "telemetry" :: "start" :: rest -> runTelemetryStart root rest
    | "ordo" :: "ingest" :: rest -> runOrdoIngest root rest
    | "ordo" :: "assess" :: rest -> runOrdoAssessment root rest
    | "ordo" :: "observe-search" :: rest -> runOrdoSearchObservation root rest
    | "ordo" :: "observe-effect" :: rest -> runOrdoEffectObservation root rest
    | "ordo" :: "current" :: rest -> runOrdoCurrent root rest
    | "ordo" :: "handoff" :: rest -> runOrdoHandoff root rest
    | "provenance" :: rest -> ProvenanceCommands.run root rest
    | "adapter" :: "call" :: rest -> runAdapterCall root rest
    | "adapter" :: "publish" :: rest -> runAdapterPublish root rest
    | _ ->
        eprintfn "%s" (fullHelp None)
        2

/// Lifecycle commands are parsed first, by a typed parser that owns its own
/// flags. Anything it does not claim -- and `status`, which it only
/// flag-checks -- falls through to the repository commands above.
let private dispatch root packageRoot arguments =
    match Lifecycle.parse packageRoot arguments with
    | Some(Error message) ->
        eprintfn "ERROR %s" message
        eprintfn "%s" (fullHelp (arguments |> List.tryHead))
        Ros.Domain.Lifecycle.ExitCode.InvalidArguments
    | Some(Ok command) ->
        match Lifecycle.run fullHelp root command with
        | Some code -> code
        | None -> repositoryDispatch root packageRoot arguments
    | None -> repositoryDispatch root packageRoot arguments

let private execute arguments =
    match parseGlobals arguments with
    | Error message ->
        eprintfn "ERROR %s" message
        1
    | Ok(root, packageRoot, remaining) -> dispatch root packageRoot remaining

[<EntryPoint>]
let main arguments =
    let version =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
        |> Option.ofObj
        |> Option.map string

    let config = Aegis.configure "Praxis.Cli" version [ Sinks.console ]

    match Bootstrap.validate None config with
    | Result.Error problems ->
        for problem in problems do
            let _, message = Bootstrap.describe problem
            eprintfn "ERROR Aegis configuration: %s" message

        1
    | Ok validated ->
        let scope = Aegis.scope validated "Praxis.Cli.Main" Map.empty

        let classify scope ex =
            Aegis.faultOf
                validated
                scope
                (FaultCode "PRAXIS.CLI.UNHANDLED")
                UnknownFailure
                FaultSeverity.Error
                DegradedApplication
                RequiresIntervention
                ManualIntervention
                "Praxis encountered an unexpected operational failure."
                ex

        match Aegis.capture validated scope classify (fun () -> execute arguments) with
        | Ok exitCode -> exitCode
        | Result.Error fault ->
            eprintfn "ERROR %s Reference %s" fault.UserMessage fault.Id.Value
            1
