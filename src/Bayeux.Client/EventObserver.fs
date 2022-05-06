namespace SfSync.Bayeux.Client

open System
open Newtonsoft.Json.Linq
open System.Threading.Tasks
open System.Threading

// On defining .NET events
// https://docs.microsoft.com/en-us/dotnet/standard/design-guidelines/event
// https://stackoverflow.com/questions/3880789/why-should-we-use-eventhandler
// https://docs.microsoft.com/en-us/dotnet/csharp/programming-guide/events/how-to-publish-events-that-conform-to-net-framework-guidelines
type MessageReceivedArgs (ev : JObject) =
    inherit EventArgs ()
    // https://docs.cometd.org/current/reference/#_code_data_code
    // The data message field is an arbitrary JSON encoded *object*
    member __.Data = ev.["data"] :?> JObject
    member __.Channel = ev.["channel"].ToString()
    member __.Message = ev
    override __.ToString () = ev.ToString ()

type MessageObserver (eventTaskScheduler : TaskScheduler, cancellationToken : CancellationToken) =
    let messageReceived = Event<MessageReceivedArgs> ()
    [<CLIEvent>]
    member __.MessageReceived = messageReceived.Publish

    member internal __.RunInEventTaskSchedulerAsync cancellationToken (action : unit -> unit) =
        Task.Factory.StartNew(action, cancellationToken, TaskCreationOptions.DenyChildAttach, eventTaskScheduler)

    interface IObserver<JObject> with
        member this.OnNext message =
            (fun () -> messageReceived.Trigger (MessageReceivedArgs message))
            |> this.RunInEventTaskSchedulerAsync cancellationToken
            |> ignore
        member this.OnCompleted () = ()
        member this.OnError ex = raise ex
