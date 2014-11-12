﻿namespace Nessos.MBrace.Runtime

open System
open System.Reflection
//open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

/// Replacement for System.Runtime.ExceptionServices.ExceptionDispatchInfo
/// that is serializable and permits symbolic stacktrace appending
[<Sealed; AutoSerializable(true)>]
type ExceptionDispatchInfo private (sourceExn : exn, remoteStackTrace : string) =

//    static let remoteTraceTable = new ConditionalWeakTable<exn, string ref>()

    // ExceptionDispatchInfo leaks mutable state in the form of exception instances
    // For reasons of sanity, copies of ExceptionDispatchInfo can only export state once,
    // or deep cloned into a separate instance.
    // This ensures correct use without the need to add serialization/deep cloning
    // dependencies to the core library

    [<NonSerialized; VolatileField>]
    let mutable isConsumed = 0
    let acquire () = 
        if Interlocked.CompareExchange(&isConsumed, 1, 0) = 0 then ()
        else
            invalidOp "ExceptionDispatchInfo instance has already been consumed."

    [<Literal>]
    static let separator = "--- End of stack trace from previous location where exception was thrown ---"

    // resolve the internal stacktrace field in exception
    // this is implementation-sensitive so not guaranteed to work. 
    static let remoteStackTraceField : FieldInfo =
        let bfs = BindingFlags.NonPublic ||| BindingFlags.Instance
        match typeof<System.Exception>.GetField("remote_stack_trace", bfs) with
        | null -> typeof<System.Exception>.GetField("_remoteStackTraceString", bfs)
        | f -> f

    static let trySetRemoteStackTraceField (trace : string) (e : exn) =
        match remoteStackTraceField with
        | null -> false
        | f -> remoteStackTraceField.SetValue(e, trace) ; true

    /// <summary>
    ///     Captures the provided exception stacktrace into an ExceptionDispatchInfo instance.
    /// </summary>
    /// <param name="exn">Captured exception</param>
    static member Capture(exn : exn) = new ExceptionDispatchInfo(exn, exn.StackTrace)

    /// <summary>
    ///     Returns the source augmented with the remote stack trace.
    /// </summary>
    /// <param name="useSeparator">Add a separator after remote stacktrace. Defaults to true.</param>
    member __.Reify (useSeparator) : exn =
        acquire ()
        let newTrace =
            if useSeparator then
                sprintf "%s%s%s%s" remoteStackTrace Environment.NewLine separator Environment.NewLine
            else
                remoteStackTrace + Environment.NewLine

        let _ = trySetRemoteStackTraceField newTrace sourceExn
        sourceExn

    member __.SourceException = sourceExn
    member __.StackTrace = remoteStackTrace

    member internal __.IsMatchingException(exn : exn) =
        obj.ReferenceEquals(sourceExn, exn) && remoteStackTrace = exn.StackTrace

    /// <summary>
    ///     Creates a new ExceptionDispatchInfo instance with line appended to stacktrace.
    /// </summary>
    /// <param name="line">Line to be appended.</param>
    member __.AppendToStackTrace(line : string) =
        acquire()
        let newTrace = sprintf "%s%s%s" remoteStackTrace Environment.NewLine line
        new ExceptionDispatchInfo(sourceExn, newTrace)

    /// <summary>
    ///     Creates a new ExceptionDispatchInfo instance with line appended to stacktrace.
    /// </summary>
    /// <param name="line">Line to be appended.</param>
    member __.AppendToStackTrace(lines : seq<string>) =
        let line = String.concat Environment.NewLine lines
        __.AppendToStackTrace line

[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ExceptionDispatchInfo =

    /// <summary>
    ///     Raise provided exception dispatch info.
    /// </summary>
    /// <param name="useSeparator">Appends a stacktrace separator after the remote stacktrace.</param>
    /// <param name="edi">Exception dispatch info to be raised.</param>
    let inline raise useSeparator (edi : ExceptionDispatchInfo) =
        raise <| edi.Reify(useSeparator)

    /// <summary>
    ///     Immediately raises exception instance, preserving its current stacktrace
    /// </summary>
    /// <param name="useSeparator">Appends a stacktrace separator after the remote stacktrace.</param>
    /// <param name="exn">Input exception.</param>
    let inline raiseWithCurrentStackTrace useSeparator (exn : 'exn) =
        let edi = ExceptionDispatchInfo.Capture exn in raise useSeparator edi

[<AutoOpen>]
module ExceptionDispatchInfoUtils =

    type Async =

        /// <summary>
        ///     Efficiently reraise exception, without losing its existing stacktrace.
        /// </summary>
        /// <param name="e"></param>
        static member Raise<'T> (e : exn) : Async<'T> = Async.FromContinuations(fun (_,ec,_) -> ec e)

        /// <summary>
        ///     Runs the asynchronous computation and awaits its result.
        ///     Preserves original stacktrace for any exception raised.
        /// </summary>
        /// <param name="workflow">Workflow to be run.</param>
        /// <param name="cancellationToken">Optioncal cancellation token.</param>
        static member RunSync(workflow : Async<'T>, ?cancellationToken) =
            let tcs = new TaskCompletionSource<Choice<'T,exn,OperationCanceledException>>()
            let inline commit f r = tcs.SetResult(f r)
            Trampoline.QueueWorkItem(fun () ->
                Async.StartWithContinuations(workflow, 
                    commit Choice1Of3, commit Choice2Of3, commit Choice3Of3, 
                    ?cancellationToken = cancellationToken))

            match tcs.Task.Result with
            | Choice1Of3 t -> t
            | Choice2Of3 e -> ExceptionDispatchInfo.raiseWithCurrentStackTrace false e
            | Choice3Of3 e -> ExceptionDispatchInfo.raiseWithCurrentStackTrace false e