namespace Ros.Infrastructure.Git

open System
open System.ComponentModel
open System.Diagnostics
open Ros.Application.Git
open Ros.Domain.Git

[<RequireQualifiedAccess>]
module GitStatusParser =
    let private failure message =
        { Operation = "parse git status"
          Reason = GitUnavailableReason.MalformedOutput
          Message = message
          ExitCode = None }

    let private delta value =
        match value with
        | ' ' -> GitDelta.Unmodified
        | 'A' -> GitDelta.Added
        | 'M' -> GitDelta.Modified
        | 'D' -> GitDelta.Deleted
        | 'R' -> GitDelta.Renamed
        | 'C' -> GitDelta.Copied
        | 'T' -> GitDelta.TypeChanged
        | 'U' -> GitDelta.Unmerged
        | other -> GitDelta.Unknown other

    let private compareChange (left: GitChange) (right: GitChange) =
        let pathComparison = StringComparer.Ordinal.Compare(left.Path, right.Path)

        if pathComparison <> 0 then
            pathComparison
        else
            StringComparer.Ordinal.Compare(
                left.OriginalPath |> Option.defaultValue "",
                right.OriginalPath |> Option.defaultValue ""
            )

    let parse (output: string) =
        if output.Length = 0 then
            Ok []
        elif output[output.Length - 1] <> '\000' then
            Error(failure "porcelain-v1 -z output did not end with a NUL delimiter")
        else
            let fields = output.Split('\000') |> Array.toList |> List.rev |> List.tail |> List.rev

            let rec read (changes: GitChange list) (remaining: string list) =
                match remaining with
                | [] -> Ok(changes |> List.rev |> List.sortWith compareChange)
                | entry :: rest when entry.Length < 3 || entry[2] <> ' ' ->
                    Error(failure "porcelain-v1 entry did not contain a two-character status and path")
                | entry :: rest ->
                    let code = entry.Substring(0, 2)
                    let path = entry.Substring(3)

                    if path.Length = 0 then
                        Error(failure "porcelain-v1 entry contained an empty path")
                    else
                        let status =
                            match code with
                            | "??" -> GitChangeStatus.Untracked
                            | "!!" -> GitChangeStatus.Ignored
                            | _ -> GitChangeStatus.Tracked(delta code[0], delta code[1])

                        let hasOrigin = code.IndexOfAny([| 'R'; 'C' |]) >= 0

                        match hasOrigin, rest with
                        | true, originalPath :: tail when originalPath.Length > 0 ->
                            read
                                ({ Status = status
                                   Path = path
                                   OriginalPath = Some originalPath }
                                 :: changes)
                                tail
                        | true, _ -> Error(failure $"rename/copy entry '{path}' did not include its original path")
                        | false, _ ->
                            read
                                ({ Status = status
                                   Path = path
                                   OriginalPath = None }
                                 :: changes)
                                rest

            read [] fields

type private ProcessResult =
    { ExitCode: int
      Output: string
      Error: string }

[<RequireQualifiedAccess>]
module ProcessGitRepository =
    let private unavailable reason message exitCode =
        GitStatusObservation.Unavailable
            { Operation = "git status"
              Reason = reason
              Message = message
              ExitCode = exitCode }

    let private runGit executable root (operation: string) (arguments: string list) : Result<ProcessResult, GitFailure> =
        try
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- executable
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.ArgumentList.Add "-C"
            startInfo.ArgumentList.Add root

            for argument in arguments do
                startInfo.ArgumentList.Add argument

            use child = new Process(StartInfo = startInfo)

            if not (child.Start()) then
                Error
                    { Operation = operation
                      Reason = GitUnavailableReason.ToolUnavailable
                      Message = "git process did not start"
                      ExitCode = None }
            else
                let standardOutput = child.StandardOutput.ReadToEndAsync()
                let standardError = child.StandardError.ReadToEndAsync()
                child.WaitForExit()

                Ok
                    { ExitCode = child.ExitCode
                      Output = standardOutput.Result
                      Error = standardError.Result.Trim() }
        with
        | :? Win32Exception as error ->
            Error
                { Operation = operation
                  Reason = GitUnavailableReason.ToolUnavailable
                  Message = error.Message
                  ExitCode = None }
        | error ->
            Error
                { Operation = operation
                  Reason = GitUnavailableReason.CommandFailed
                  Message = error.Message
                  ExitCode = None }

    let private observe executable root () =
        match runGit executable root "git status" [ "status"; "--porcelain=v1"; "-z"; "--untracked-files=all" ] with
        | Error failure -> GitStatusObservation.Unavailable failure
        | Ok result when result.ExitCode <> 0 ->
            let reason =
                if result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) then
                    GitUnavailableReason.NotRepository
                else
                    GitUnavailableReason.CommandFailed

            let message = if result.Error.Length = 0 then $"git exited with code {result.ExitCode}" else result.Error
            unavailable reason message (Some result.ExitCode)
        | Ok result ->
            match GitStatusParser.parse result.Output with
            | Error failure -> GitStatusObservation.Unavailable failure
            | Ok [] -> GitStatusObservation.Clean
            | Ok changes -> GitStatusObservation.Changed changes

    /// Mirrors production's optional `ROS_BASE_REF` extension in `gitPaths`:
    /// a ref that does not resolve to an existing commit is a silent
    /// `RefUnavailable` (a CI base ref can be absent in nested fixture
    /// repositories), never a hard failure; only a resolvable ref whose
    /// `diff --name-only` itself fails is `Unavailable`.
    let private compareBase executable root (ref: string option) : GitBaseComparisonOutcome =
        match ref with
        | None -> GitBaseComparisonOutcome.NotConfigured
        | Some baseRef ->
            match runGit executable root "git cat-file" [ "cat-file"; "-e"; $"{baseRef}^{{commit}}" ] with
            | Error _ -> GitBaseComparisonOutcome.RefUnavailable
            | Ok existsResult when existsResult.ExitCode <> 0 -> GitBaseComparisonOutcome.RefUnavailable
            | Ok _ ->
                match runGit executable root "git diff" [ "diff"; "--name-only"; $"{baseRef}...HEAD" ] with
                | Error failure -> GitBaseComparisonOutcome.Unavailable failure
                | Ok diffResult when diffResult.ExitCode <> 0 ->
                    let message =
                        if diffResult.Error.Length = 0 then
                            $"git exited with code {diffResult.ExitCode}"
                        else
                            diffResult.Error

                    GitBaseComparisonOutcome.Unavailable
                        { Operation = "git diff"
                          Reason = GitUnavailableReason.CommandFailed
                          Message = message
                          ExitCode = Some diffResult.ExitCode }
                | Ok diffResult ->
                    diffResult.Output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.toList
                    |> GitBaseComparisonOutcome.Committed

    let createWithExecutable executable root : GitRepository =
        { ObserveStatus = observe executable (IO.Path.GetFullPath root) }

    let create root = createWithExecutable "git" root

    let createBaseComparisonWithExecutable executable root : GitBaseComparison =
        { Compare = compareBase executable (IO.Path.GetFullPath root) }

    let createBaseComparison root = createBaseComparisonWithExecutable "git" root

    /// Mirrors production `gitSnapshot`'s branch/commit reads
    /// (`tools/ros_telemetry.mjs`): `git branch --show-current` and `git
    /// rev-parse HEAD`, each folded to `None` on any failure (a non-zero
    /// exit, or the tool itself being unavailable) rather than surfaced as
    /// an error -- telemetry capture never blocks on a broken worktree.
    let private readText executable root arguments =
        match runGit executable root "git" arguments with
        | Ok result when result.ExitCode = 0 ->
            let value = result.Output.Trim()
            if value.Length > 0 then Some value else None
        | _ -> None

    let readBranchAndCommitWithExecutable executable root : string option * string option =
        let fullRoot = IO.Path.GetFullPath root
        readText executable fullRoot [ "branch"; "--show-current" ], readText executable fullRoot [ "rev-parse"; "HEAD" ]

    let readBranchAndCommit root = readBranchAndCommitWithExecutable "git" root

    /// Mirrors production `runGitText`'s default (trimmed) success/failure
    /// shape for the git-diff-derived change-summary reads
    /// `cleanBaselineChanges` performs: `git diff --name-status`, `git diff
    /// --numstat`, and `git rev-list --count`.
    let private runGitTextTrimmed executable root operation (arguments: string list) : Result<string, GitFailure> =
        match runGit executable root operation arguments with
        | Error failure -> Error failure
        | Ok result when result.ExitCode <> 0 ->
            let reason =
                if result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) then
                    GitUnavailableReason.NotRepository
                else
                    GitUnavailableReason.CommandFailed

            let message = if result.Error.Length = 0 then $"git exited with code {result.ExitCode}" else result.Error
            Error { Operation = operation; Reason = reason; Message = message; ExitCode = Some result.ExitCode }
        | Ok result -> Ok(result.Output.Trim())

    let readNameStatusDiffWithExecutable executable root (startCommit: string) : Result<string, GitFailure> =
        runGitTextTrimmed executable (IO.Path.GetFullPath root) "git diff" [ "diff"; "--name-status"; "--find-renames"; startCommit ]

    let readNameStatusDiff root startCommit = readNameStatusDiffWithExecutable "git" root startCommit

    let readNumstatDiffWithExecutable executable root (startCommit: string) : Result<string, GitFailure> =
        runGitTextTrimmed executable (IO.Path.GetFullPath root) "git diff" [ "diff"; "--numstat"; "--find-renames"; startCommit ]

    let readNumstatDiff root startCommit = readNumstatDiffWithExecutable "git" root startCommit

    /// `-z`-delimited output is never trimmed of a real trailing NUL, matching
    /// production's own `{trim: false}` override for this one read.
    let readUntrackedFilesWithExecutable executable root : Result<string, GitFailure> =
        match runGit executable (IO.Path.GetFullPath root) "git ls-files" [ "ls-files"; "--others"; "--exclude-standard"; "-z" ] with
        | Error failure -> Error failure
        | Ok result when result.ExitCode <> 0 ->
            let message = if result.Error.Length = 0 then $"git exited with code {result.ExitCode}" else result.Error
            Error { Operation = "git ls-files"; Reason = GitUnavailableReason.CommandFailed; Message = message; ExitCode = Some result.ExitCode }
        | Ok result -> Ok result.Output

    let readUntrackedFiles root = readUntrackedFilesWithExecutable "git" root

    let readCommitCountWithExecutable executable root (startCommit: string) (endCommit: string) : Result<int, GitFailure> =
        runGitTextTrimmed executable (IO.Path.GetFullPath root) "git rev-list" [ "rev-list"; "--count"; $"{startCommit}..{endCommit}" ]
        |> Result.map int

    let readCommitCount root startCommit endCommit = readCommitCountWithExecutable "git" root startCommit endCommit

    /// `git show REVISION:PATH` verbatim, or `None` when the path did not
    /// exist at that revision (or Git is unavailable). Used to compare an
    /// artifact's provenance with its base-revision version.
    let readFileAtRevision root (revision: string) (relativePath: string) : string option =
        match runGit "git" (IO.Path.GetFullPath root) "git show" [ "show"; $"{revision}:{relativePath.Replace('\\', '/')}" ] with
        | Ok result when result.ExitCode = 0 -> Some result.Output
        | _ -> None

    let commitExistsWithExecutable executable root (commit: string) =
        match runGit executable (IO.Path.GetFullPath root) "git cat-file" [ "cat-file"; "-e"; $"{commit}^{{commit}}" ] with
        | Ok result -> result.ExitCode = 0
        | Error _ -> false

    let commitExists root commit = commitExistsWithExecutable "git" root commit
