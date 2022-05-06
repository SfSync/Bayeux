namespace SfSync.Bayeux.Client

open System

[<Serializable>]
type BayeuxProtocolException (message) = inherit Exception(message)

[<Serializable>]
type BayeuxRequestException (message) = inherit Exception(message)

[<Serializable>]
type BayeuxTransportException (message : string, innerException, isTransportClosed) =
    inherit Exception (message, innerException)
    member __.IsTransportClosed : bool = isTransportClosed

module ChannelFields =
    let Meta = "/meta"
    let MetaHandshake = Meta + "/handshake"
    let MetaConnect = Meta + "/connect"
    let MetaSubscribe = Meta + "/subscribe"
    let MetaUnsubscribe = Meta + "/unsubscribe"
    let MetaDisconnect = Meta + "/disconnect"
    let MetaPublish = Meta + "/publish"
    let MetaUnsuccessful = Meta + "/unsuccessful"

open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Newtonsoft.Json.Linq

type internal IBayeuxTransport =
    inherit IDisposable

    abstract OpenAsync : cancellationToken:CancellationToken -> Task
    abstract RequestAsync : requests:obj seq *  cancellationToken:CancellationToken -> Task<JObject>
    abstract CloseAsync : unit -> Task


type HttpLongPollingTransportOptions =
    {
        HttpClientFactory : Func<HttpClient>
        Uri : Uri
        EventsObserver : IObserver<JObject>
    }

open System.Net.WebSockets

type WebSocketTransportOptions =
    {
        WebSocketFactory : Func<WebSocket> voption
        Uri : Uri
        ResponseTimeout : TimeSpan
        EventsObserver : IObserver<JObject>
    }

type Transport =
    | HttpLongPolling of Options : HttpLongPollingTransportOptions
    | WebSocket of Options : WebSocketTransportOptions

module Client =

    let private chooseEventTaskScheduler
        (logger : ILogger)
        (eventTaskScheduler : TaskScheduler Option) =
        match eventTaskScheduler with
        | Some scheduler -> scheduler
        | None ->
            match SynchronizationContext.Current with
            | null ->
                logger.LogInformation ("Using a new TaskScheduler with ordered execution for events.")
                ConcurrentExclusiveSchedulerPair().ExclusiveScheduler
            | _ ->
                logger.LogInformation ("Using current SynchronizationContext for events: {SynchronizationContext.Current}", SynchronizationContext.Current)
                TaskScheduler.FromCurrentSynchronizationContext()

    let startAsync (reconnectDelays : TimeSpan seq)
                   (eventTaskScheduler : TaskScheduler option)
                   (cancellationToken : CancellationToken)
                   (transport : Transport) =
        async {
            ()
        }

    let subscribeManyAsync (channels, cancellationToken) client = ()
    let unsubscribeManyAsync (channels, cancellationToken) client = ()
    let subscribeAsync (channel, cancellationToken) client = ()
    let unsubscribeAsync (channel, cancellationToken) client = ()
    let stopAsync (client, cancellationToken) = ()
