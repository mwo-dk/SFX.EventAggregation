module SFX.EventAggregation

open System
open System.Threading
open SFX.ROP
open SFX.EventAggregation.Model
open SFX.EventAggregation.Infrastructure

let createEventAggregator() = new EventAggregator<'a>()

type SyncHandler<'a> = 'a -> unit
type AsyncHandler<'a> = 'a -> Async<unit>
type Subscriber<'a> =
| Sync of IHandle<'a>
| SyncWithContext of IHandle<'a>*SynchronizationContext
| Async of IHandleAsync<'a>
| AsyncWithContext of IHandle<'a>*SynchronizationContext
| SyncHandler of SyncHandler<'a>
| SyncHandlerWithContext of SyncHandler<'a>*SynchronizationContext
| AsyncHandler of AsyncHandler<'a>
| AsyncHandlerWithContext of AsyncHandler<'a>*SynchronizationContext
let sync = SyncHandler
let syncWithContext x y = SyncHandlerWithContext(x,y)
let async = AsyncHandler
let asyncWithContext x y = AsyncHandlerWithContext(x,y)

type Subscription = int64

type EventAggregatorError =
| LockNotAcquired
| EventAggregatorNotFound
| SubscriptionNotFound of Subscription

type Handle<'a>(handler: SyncHandler<'a>) =
    interface IHandle<'a> with
        member _.Handle message = message |> handler
let createHandle x = Handle(x)
type AsyncHandle<'a>(handler: AsyncHandler<'a>) =
    interface IHandleAsync<'a> with
        member _.HandleAsync message = 
            Tasks.Task.Run(fun () -> 
                message |> handler |> Async.RunSynchronously)
let createAsyncHandle x = AsyncHandle(x)

// This tiny infrastructure is in order to hold on to else temporary objects created to be
// registered inside event aggregators - in the functional cases.
let mutable private lock = SpinLock()
let mutable private hiddenEntities : (WeakReference * Map<Subscription, obj>) list = List.empty
let private addHiddenEntity id x evtAgg =
    let mutable lockTaken = false
    try 
        lock.TryEnter(&lockTaken)
        if lockTaken |> not then
            LockNotAcquired |> fail
        else
            // Clean dead evt aggs
            hiddenEntities <- hiddenEntities |> List.filter (fun (e,_) -> e.Target <> null)
            match hiddenEntities |> List.tryFind (fun (e, _) -> obj.ReferenceEquals(e.Target, evtAgg)) with
            | Some slot -> 
                let e, m = slot
                let m = m |> Map.add id (x :> obj)
                hiddenEntities <- (e,m)::(hiddenEntities |> List.except [slot])
            | _ ->
                let m = Map.empty |> Map.add id (x :> obj)
                hiddenEntities <- (WeakReference(evtAgg),m)::hiddenEntities
            () |> succeed
    finally
        if lockTaken then
            lock.Exit(false)
let private removeHiddenEntity id evtAgg =
    let mutable lockTaken = false
    try 
        lock.TryEnter(&lockTaken)
        if lockTaken |> not then
            LockNotAcquired |> fail
        else 
            // Clean dead evt aggs
            hiddenEntities <- hiddenEntities |> List.filter (fun (e,_) -> e.Target <> null)
            match hiddenEntities |> List.tryFind (fun (e, _) -> obj.ReferenceEquals(e.Target, evtAgg)) with
            | Some slot -> 
                let e, m = slot
                let m = m |> Map.remove id
                hiddenEntities <- (e,m)::(hiddenEntities |> List.except [slot])
                () |> succeed
            | _ -> EventAggregatorNotFound |> fail
    finally
        if lockTaken then
            lock.Exit(false)

let subscribe serialize handler (evtAgg: IEventAggregator<'a>) =
    let doSyncHandler x s =
        let x = Handle(x)
        let id = evtAgg.Subscribe(x, s, serialize)
        match evtAgg |> addHiddenEntity id x with
        | Success _ -> id |> succeed
        | Failure error -> error |> fail
    let doAsyncHandler x s =
        let x = AsyncHandle(x)
        let id = evtAgg.Subscribe(x, s, serialize)
        match evtAgg |> addHiddenEntity id x with
        | Success _ -> id |> succeed
        | Failure error -> error |> fail
    match handler with
    | Sync x -> evtAgg.Subscribe(x, null, serialize) |> succeed
    | SyncWithContext (x,s) -> evtAgg.Subscribe(x, s, serialize) |> succeed
    | Async x -> evtAgg.Subscribe(x, null, serialize) |> succeed
    | AsyncWithContext (x,s) -> evtAgg.Subscribe(x, s, serialize) |> succeed
    | SyncHandler x -> doSyncHandler x null
    | SyncHandlerWithContext (x, s) -> doSyncHandler x s
    | AsyncHandler x -> doAsyncHandler x null
    | AsyncHandlerWithContext (x, s) -> doAsyncHandler x s
        
let unsubscribe id (evtAgg: IEventAggregator<'a>) =
    if evtAgg.Unsubscribe id then
        match evtAgg |> removeHiddenEntity id with
        | Success _ -> () |> succeed
        | Failure error ->
            match error with
            | LockNotAcquired -> LockNotAcquired |> fail
            | _ -> () |> succeed
    else id |> SubscriptionNotFound |> fail

let publish message (evtAgg: IEventAggregator<'a>) = evtAgg.Publish message

let createEventAggregatorRepository() =
    EventAggregatorRepository()
let initializeEventAggregatorRepository (repo: IEventAggregatorRepository) =
    repo.Initialize()
let isEventAggregatorRepositoryInitialized (repo: IEventAggregatorRepository) =
    repo.IsInitialized()

type EventAggregatorInstance =
| Anonymous
| Named of string
let getEventAggregator instance (repo: IEventAggregatorRepository) = 
    match instance with
    | Anonymous -> repo.GetEventAggregator()
    | Named name -> repo.GetEventAggregator(Name(name))