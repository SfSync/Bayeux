module Sfsync.Bayeux.Sample.Program

open System
open System.Net.Http
open System.Reactive
open System.Threading
open Microsoft.Extensions.Logging
open Newtonsoft.Json.Linq
open Sfsync.Bayeux.Client

let streamingUri = Uri "<uri>"
let onNext (message : JObject) = ()
let onError (exn : exn) = ()
let observer = Observer.Create (onNext, onError)
let clientOptions : HttpLongPollingTransportOptions =
    { HttpClientFactory = Func<HttpClient>(fun () -> new HttpClient())
      Uri = streamingUri
      EventsObserver = observer }
let loggerFactory = new LoggerFactory()
let connection = new BayeuxConnection (clientOptions, ReconnectDelays (), loggerFactory)

let startAsync (channels, cancellationToken) =
    async {
        do! connection.StartAsync cancellationToken
        return! connection.SubscribeAsync (channels, cancellationToken)
    }

[<EntryPoint>]
let main argv =
    let result = Async.RunSynchronously (startAsync (seq { struct("<channel>", ValueNone)}, CancellationToken.None))
    Console.ReadKey () |> ignore;
    0 // return an integer exit code
