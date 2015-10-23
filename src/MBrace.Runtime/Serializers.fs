﻿namespace MBrace.Runtime

open System
open System.IO
open System.Runtime.Serialization
open System.Collections.Concurrent

open Nessos.FsPickler
open Nessos.FsPickler.Json

open Newtonsoft.Json

open MBrace.Core.Internals

/// Abstract FsPickler ISerializer implementation
[<AbstractClass; AutoSerializable(true)>]
type FsPicklerStoreSerializer internal () =

    [<NonSerialized>]
    let mutable localInstance : FsPicklerSerializer option = None

    /// Serializer identifier
    abstract Id : string
    /// Creates a new FsPickler serializer instance corresponding to the implementation.
    abstract CreateSerializer : unit -> FsPicklerSerializer

    member s.Serializer =
        match localInstance with
        | Some instance -> instance
        | None ->
            let instance = s.CreateSerializer()
            localInstance <- Some instance
            instance

    interface ISerializer with
        member s.Id = s.Id
        member s.IsSerializable(value : 'T) = try FsPickler.EnsureSerializable value ; true with _ -> false
        member s.Serialize (target : Stream, value : 'T, leaveOpen : bool) = s.Serializer.Serialize(target, value, leaveOpen = leaveOpen)
        member s.Deserialize<'T>(stream, leaveOpen) = s.Serializer.Deserialize<'T>(stream, leaveOpen = leaveOpen)
        member s.SeqSerialize(stream, values : 'T seq, leaveOpen) = s.Serializer.SerializeSequence(stream, values, leaveOpen = leaveOpen)
        member s.SeqDeserialize<'T>(stream, leaveOpen) = s.Serializer.DeserializeSequence<'T>(stream, leaveOpen = leaveOpen)
        member s.ComputeObjectSize<'T>(graph:'T) = s.Serializer.ComputeSize graph
        member s.Clone(graph:'T) = FsPickler.Clone graph


[<AbstractClass; AutoSerializable(true)>]
type FsPicklerStoreTextSerializer internal () =
    inherit FsPicklerStoreSerializer()

    [<NonSerialized>]
    let mutable localInstance : FsPicklerTextSerializer option = None

    member __.TextSerializer =
        match localInstance with
        | Some l -> l
        | None ->
            match base.Serializer with
            | :? FsPicklerTextSerializer as ts -> localInstance <- Some ts ; ts
            | _ -> invalidOp <| sprintf "Serializer '%s' is not a text serializer." __.Id

    interface ITextSerializer with
        member s.TextDeserialize<'T>(source: TextReader, leaveOpen: bool): 'T = s.TextSerializer.Deserialize<'T>(source, leaveOpen = leaveOpen)
        member s.TextSerialize<'T>(target: TextWriter, value: 'T, leaveOpen: bool): unit = s.TextSerializer.Serialize<'T>(target, value, leaveOpen = leaveOpen)

/// Serializable binary FsPickler implementation of ISerializer that targets
/// the underlying Vagabond registry
[<Sealed; AutoSerializable(true)>]
type VagabondFsPicklerBinarySerializer () =
    inherit FsPicklerStoreSerializer()
    do ignore VagabondRegistry.Instance

    override __.Id = "Vagabond FsPickler binary serializer"
    override __.CreateSerializer () = 
        VagabondRegistry.Instance.Serializer :> _


/// Serializable xml FsPickler implementation of ISerializer that targets
/// the underlying Vagabond registry
[<Sealed; AutoSerializable(true)>]
type VagabondFsPicklerXmlSerializer (?indent : bool) =
    inherit FsPicklerStoreTextSerializer()
    do ignore VagabondRegistry.Instance

    override __.Id = "Vagabond FsPickler xml serializer"
    override __.CreateSerializer () = 
        FsPickler.CreateXmlSerializer(typeConverter = VagabondRegistry.Instance.TypeConverter, ?indent = indent) :> _

/// Serializable json FsPickler implementation of ISerializer that targets
/// the underlying Vagabond registry
[<Sealed; AutoSerializable(true)>]
type VagabondFsPicklerJsonSerializer (?omitHeader : bool, ?indent : bool) =
    inherit FsPicklerStoreTextSerializer()

    override __.Id = "Vagabond FsPickler json serializer"
    override __.CreateSerializer () = 
        FsPickler.CreateJsonSerializer(typeConverter = VagabondRegistry.Instance.TypeConverter, ?indent = indent, ?omitHeader = omitHeader) :> _

/// Json.Net ISerializer implementation
[<Sealed; AutoSerializable(true)>]
type JsonDotNetSerializer() =
    [<NonSerialized>]
    let mutable localInstance : JsonSerializer option = None

    member s.Serializer =
        match localInstance with
        | Some instance -> instance
        | None ->   
            let s = JsonSerializer.CreateDefault()
            localInstance <- Some s
            s

    interface ITextSerializer with
        member s.Id: string = "Newtonsoft.Json"
        member s.Clone(_graph: 'T): 'T = raise <| new NotSupportedException()
        member s.ComputeObjectSize(_graph: 'T): int64 = raise <| new NotSupportedException()
        member s.IsSerializable(_value: 'T): bool = raise <| new NotSupportedException()
        member s.SeqSerialize(_target: Stream, _values: seq<'T>, _leaveOpen: bool): int = raise <| new NotSupportedException()
        member s.SeqDeserialize(_source: Stream, _leaveOpen: bool): seq<'T> = raise <| new NotSupportedException()

        member s.Serialize(target: Stream, value: 'T, leaveOpen: bool): unit = 
            let writer = new StreamWriter(target)
            use _d = if leaveOpen then null else writer
            s.Serializer.Serialize(writer, value)

        member s.Deserialize<'T>(source: Stream, leaveOpen: bool): 'T = 
            let reader = new StreamReader(source)
            use _d = if leaveOpen then null else reader
            s.Serializer.Deserialize(reader, typeof<'T>) :?> 'T

        member s.TextSerialize(target: TextWriter, value: 'T, leaveOpen: bool): unit = 
            use _d = if leaveOpen then null else target
            s.Serializer.Serialize(target, value)

        member s.TextDeserialize(source: TextReader, leaveOpen: bool): 'T = 
            use _d = if leaveOpen then null else source
            s.Serializer.Deserialize(source, typeof<'T>) :?> 'T