﻿namespace MBrace.Thespian.Runtime


// Standard immutable queue implementation
type ImmutableQueue<'T> private (front : 'T list, back : 'T list) =
    /// Gets an empty queue
    static member Empty : ImmutableQueue<'T> = new ImmutableQueue<'T>([],[])
    /// Gets the current element count of the queue
    member __.Count : int = front.Length + back.Length
    /// Returns true if queue is empty
    member __.IsEmpty : bool = List.isEmpty front && List.isEmpty back
    /// Creates a new queue with element appended to the original
    member __.Enqueue (t : 'T) : ImmutableQueue<'T> = new ImmutableQueue<'T>(front, t :: back)
    /// Creates a new queue with multiple elements appended
    member __.EnqueueMultiple (ts : 'T list) : ImmutableQueue<'T> = new ImmutableQueue<'T>(front, List.rev ts @ back) 
    /// Attempt to dequeue, returning None if empty.
    member __.TryDequeue () : ('T * ImmutableQueue<'T>) option =
        match front with
        | hd :: tl -> Some(hd, new ImmutableQueue<'T>(tl, back))
        | [] -> 
            match List.rev back with
            | [] -> None
            | hd :: tl -> Some(hd, new ImmutableQueue<'T>(tl, []))
    
    /// Returns an enumeration of the queue elements
    member __.ToSeq() : seq<'T> = seq { yield! front ; yield! List.rev back }


/// Immutable queue implementation with messages indexable by topic
type TopicQueue<'Topic, 'T when 'Topic : comparison> = 
    {
        GlobalQueue : ImmutableQueue<'T>
        TopicQueues : Map<'Topic, ImmutableQueue<'T>>
    }
with
    /// Creates an empty queue
    static member Empty : TopicQueue<'Topic, 'T> = { GlobalQueue = ImmutableQueue.Empty ; TopicQueues = Map.empty }

    /// <summary>
    ///     Enqueue a new item to queue, optionally with a specified topic.
    /// </summary>
    /// <param name="t">Item to be enqueued</param>
    /// <param name="topic">Optional topic identifier. Defaults to no topic.</param>
    member state.Enqueue(t : 'T, ?topic : 'Topic) : TopicQueue<'Topic, 'T> =
        match topic with
        | None -> { state with GlobalQueue = state.GlobalQueue.Enqueue t }
        | Some tp -> 
            match state.TopicQueues.TryFind tp with
            | None ->
                let queue = ImmutableQueue<'T>.Empty.Enqueue t
                { state with TopicQueues = state.TopicQueues.Add(tp, queue) }
            | Some qr ->
                let qr' = qr.Enqueue t
                { state with TopicQueues = state.TopicQueues.Add(tp, qr') }

    /// <summary>
    ///     Dequeue a message from queue, optionally declaring a topic.
    /// </summary>
    /// <param name="topic">Optional topic to be dequeued.</param>
    member state.TryDequeue(?topic : 'Topic) : ('T * TopicQueue<'Topic, 'T>) option =
        let inline dequeueGlobal() =
            match state.GlobalQueue.TryDequeue() with
            | None -> None
            | Some(msg, gq') -> Some (msg, { state with GlobalQueue = gq' })

        match topic with
        | None -> dequeueGlobal()
        | Some tp ->
            match state.TopicQueues.TryFind tp with
            | None -> dequeueGlobal()
            | Some queue ->
                match queue.TryDequeue() with
                | None -> dequeueGlobal()
                | Some(msg, gq') -> Some(msg, { state with TopicQueues = state.TopicQueues.Add(tp, gq')})

    /// <summary>
    ///     Perform cleanup of the queue, by moving all elements from 
    ///     cleaned up topics to the global queue.
    /// </summary>
    /// <param name="cleanup">Topic cleanup decision predicate.</param>
    member state.Cleanup(cleanup : 'Topic -> bool) =
        let pruned = new ResizeArray<'T> ()
        let topics =
            state.TopicQueues
            |> Map.filter(fun t q -> 
                            if q.IsEmpty then false
                            elif cleanup t then
                                pruned.AddRange (q.ToSeq())
                                false
                            else
                                true)

        pruned.ToArray(), { state with TopicQueues = topics }