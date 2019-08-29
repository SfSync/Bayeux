namespace Sfsync.Bayeux.Client

open FSharp.Core
open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.Net.Http
open System.Runtime.ExceptionServices
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open Newtonsoft.Json.Serialization

[<AutoOpen>]
module internal AsyncExceptions =
    let inline reraiseAsync ex =
        (ExceptionDispatchInfo.Capture ex).Throw ()
        Unchecked.defaultof<_>

[<Struct>]
type ConnectionErrors =
    | NoConnection

[<Struct>]
type SubscriptionResult =
    | NoChannels
    | Subscribed of Response : JObject


module MessageFields = Message.Fields

open Message

[<CLIMutable>]
[<Struct>]
type internal BayeuxResponse =
    {
        [<JsonProperty(MessageFields.Successful)>]
        Successful : bool
        [<JsonProperty(MessageFields.Error)>]
        Error : string
    }

type BayeuxConnection
    private (transport : IBayeuxTransport, connectionType : string, reconnectDelays : ReconnectDelays, logger : ILogger) =

    let serializer = JsonSerializer(ContractResolver = CamelCasePropertyNamesContractResolver())

    [<DefaultValue>]
    val mutable private pollCancel : CancellationTokenSource

    [<DefaultValue>]
    val mutable private clientId : ClientId Option
    let mutable ext = ValueNone
    let mutable pollCancelLock = obj ()

    [<VolatileField>]
    let mutable currentConnectionState = -1
    let connectionStateChanged = Event<ConnectionStateChangedArgs> ()

    let subscribedChannelsLock = obj ()
    let subscribedChannels = new Dictionary<string, int64 voption>();

    new (options : HttpLongPollingTransportOptions, reconnectDelays, loggerFactory : ILoggerFactory) =
        let transport =
            new HttpLongPollingTransport
                (options.HttpClientFactory, options.Uri, options.EventsObserver, loggerFactory.CreateLogger<HttpLongPollingTransport>())
        BayeuxConnection (transport, "long-polling", reconnectDelays, loggerFactory.CreateLogger<BayeuxConnection>())

    new (options : WebSocketTransportOptions, reconnectDelays, loggerFactory : ILoggerFactory) =
        let transport =
            new WebSocketTransport
                (options.WebSocketFactory, options.Uri, options.ResponseTimeout, options.EventsObserver, loggerFactory.CreateLogger<WebSocketTransport>())
        BayeuxConnection (transport, WebSocket.ToString(), reconnectDelays, loggerFactory.CreateLogger<BayeuxConnection>())

    member this.SubscribedChannelNames = subscribedChannels.Keys.ToImmutableHashSet()
    member this.SubscribedChannels = subscribedChannels.ToImmutableHashSet()

    [<CLIEvent>]
    member __.ConnectionStateChanged = connectionStateChanged.Publish

    let obtainAdvice lastAdvice (response : JObject) =
        let adviceToken = response.[MessageFields.Advice];
        match adviceToken with
        | null -> lastAdvice
        | _ -> adviceToken.ToObject<BayeuxAdvice>(serializer)

    let obtainAndSaveExt (response : JObject) =
        let extToken = response.[MessageFields.Ext];
        match extToken with
        | null -> ext <- ValueNone
        | _ -> ext <- ValueSome (extToken.ToObject<BayeuxExt>(serializer))

    member private this.SetConnectionState (state : ConnectionState) =
        let oldConnectionState =
            Interlocked.Exchange (&currentConnectionState, int state);

        if not (oldConnectionState = int state) then
            connectionStateChanged.Trigger (ConnectionStateChangedArgs state)

    member private this.RequestSubscriptionAsync
        (channelsToUnsubscribe, channelsToSubscribe) =
        async {
            match this.clientId with
            | Some clientId ->
                let channelsEmpty =
                    Seq.isEmpty channelsToUnsubscribe
                 && Seq.isEmpty channelsToSubscribe
                if not channelsEmpty
                    then
                        let createUnsubscribeRequest channel =
                            Unsubscribe (clientId, Subscription channel)
                        let createSubscribeRequest struct(channel, replayId) =
                            let replayExt =
                                match ext with
                                | ValueSome ext when ext.Replay -> replayId
                                | ValueSome _ | ValueNone -> ValueNone
                            Subscribe (clientId, Subscription channel, replayExt)
                        let requests =
                            seq {
                                yield channelsToUnsubscribe
                                |> Seq.map createUnsubscribeRequest
                                yield channelsToSubscribe
                                |> Seq.map createSubscribeRequest
                            }
                            |> Seq.concat
                            |> Seq.map (fun request -> request :> obj)
                        let! response = requestManyAsync requests
                        return Ok (Subscribed response)
                else
                    return Ok NoChannels
            | None ->
                return Error NoConnection
        }

    member internal this.RequestSubscribeAsync channels =
        this.RequestSubscriptionAsync (Seq.empty<string>, channels)

    member internal this.RequestUnsubscribeAsync channels =
        this.RequestSubscriptionAsync (channels, Seq.empty<struct(string * int64 voption)>)

    member private this.SubscribeImplAsync channels =
        async {
            lock subscribedChannelsLock (fun () ->
                for struct(channel, replayId) in channels do
                    subscribedChannels.[channel] <- replayId)
            return! this.RequestSubscribeAsync channels
        }

    member this.SubscribeAsync (channels, [<Optional>] cancellationToken) =
        Async.StartAsTask (this.SubscribeImplAsync channels, cancellationToken = cancellationToken)

    member this.SubscribeAsync (channel, replayId, [<Optional>] cancellationToken) =
        Async.StartAsTask (this.SubscribeImplAsync (seq { yield struct(channel, replayId) }), cancellationToken = cancellationToken)

    member private this.UnsubscribeImplAsync channels =
        async {
            lock subscribedChannelsLock (fun () ->
                for channel in channels do
                    subscribedChannels.Remove channel |> ignore)
            return! this.RequestUnsubscribeAsync channels
        }

    member this.UnsubscribeAsync (channels, [<Optional>] cancellationToken) =
        Async.StartAsTask (this.UnsubscribeImplAsync channels, cancellationToken = cancellationToken)

    member this.UnsubscribeAsync (channel, [<Optional>] cancellationToken) =
        Async.StartAsTask (this.UnsubscribeImplAsync (seq { yield channel }), cancellationToken = cancellationToken)

    let requestManyAsync requests =
        async {
            // https://docs.cometd.org/current/reference/#_messages
            // All Bayeux messages SHOULD be encapsulated in a JSON encoded array so that   multiple messages may be transported together
            let! responseObj = transport.RequestAsync (requests, Async.DefaultCancellationToken)

            //let response : BayeuxResponse =
            //    responseObj.ToString()
            //    |> Json.parse
            //    |> Json.deserialize
            let response = responseObj.ToObject<BayeuxResponse>(serializer);

            if not (response.Successful) then
                raise (BayeuxRequestException response.Error)

            return responseObj;
        }

    let requestAsync (request : obj) =
        async {
            Trace.Assert(not (request :? System.Collections.IEnumerable), "Use method  RequestMany")
            return! requestManyAsync (seq { yield request })
        }

    member private this.ConnectAsync lastAdvice =
        async {
            match this.clientId with
            | Some clientId ->
                let request = Connect (clientId, LongPolling)
                let! response = requestAsync request
                this.SetConnectionState ConnectionState.Connected
                return Ok (obtainAdvice lastAdvice response)
            | None ->
                return Error NoConnection
        }

    member private this.DisconnectAsync () =
        async {
            match this.clientId with
            | Some clientId ->
                let request = Disconnect clientId
                let! response = requestAsync request
                return Ok response
            | None ->
                return Error NoConnection
        }

    member private this.HandshakeAsync lastAdvice =
        async {
            this.SetConnectionState ConnectionState.Connecting

            let request = Request.Handshake (BayeuxVersion "1.0", SupportedConnectionTypes [ connectionType ], ValueSome true)
            let! response = requestAsync request

            let clientId = ClientId (response.[MessageFields.ClientId].Value<string>())
            Interlocked.Exchange (&this.clientId, Some clientId) |> ignore
            let! result = this.RequestSubscribeAsync (this.SubscribedChannels |> Seq.map (fun kvp -> struct(kvp.Key, kvp.Value)))
            this.SetConnectionState ConnectionState.Connected
            obtainAndSaveExt response
            return obtainAdvice lastAdvice response
        }

    member this.Dispose () =
        match this.pollCancel with
        | null -> ()
        | _ ->
            this.pollCancel.Cancel ()
            this.pollCancel.Dispose ()
        transport.Dispose ()

    let rec pollAsync
        cancellationToken
        (handshakeAsync : BayeuxAdvice -> Async<BayeuxAdvice>)
        (connectAsync : BayeuxAdvice -> Async<Result<BayeuxAdvice,ConnectionErrors>>)
        setConnectionState
        transportClosed transportFailed
        (lastAdvice : BayeuxAdvice) : Async<unit> =

        async {
            reconnectDelays.ResetIfLastSucceeded ()
            let pollAsync = pollAsync cancellationToken handshakeAsync connectAsync setConnectionState
            let pollTransportOkAsync = pollAsync transportClosed transportFailed
            try
                if transportClosed then
                    logger.LogInformation("Re-opening transport due to previously failed request.")
                    do! transport.OpenAsync cancellationToken
                    return! pollAsync false true lastAdvice
                elif transportFailed then
                    logger.LogInformation("Re-handshaking due to previously failed request.");
                    let! advice = handshakeAsync lastAdvice
                    return! pollAsync transportClosed false advice
                else
                    match lastAdvice.Reconnect with
                    | ReconnectValues.None ->
                        logger.LogInformation("Long-polling stopped on server request.")
                        return ()

                        // https://docs.cometd.org/current/reference/       #_the_code_long_polling_code_response_messages
                        // interval: the number of milliseconds the client SHOULD wait before    issuing another long poll request

                        // usual sample advice:
                        // {"interval":0,"timeout":20000,"reconnect":"retry"}
                        // another sample advice, when too much time without polling:
                        // [{"advice":{"interval":0,"reconnect":"handshake"},"channel":"/meta/   connect","error":"402::Unknown client","successful":false}]
                        logger.LogInformation("Re-handshaking after {lastAdviceInterval} ms on server request.", lastAdvice.Interval)
                    | ReconnectValues.Handshake ->
                        do! Task.Delay lastAdvice.Interval
                        let! advice = handshakeAsync lastAdvice
                        return! pollTransportOkAsync advice
                    | ReconnectValues.Retry
                    | _ ->
                        logger.LogInformation("Re-connecting after {lastAdviceInterval} ms on server request.", lastAdvice.Interval)
                        do! Task.Delay lastAdvice.Interval
                        let! advice = connectAsync lastAdvice
                        match advice with
                        | Ok advice -> return! pollTransportOkAsync advice
                        | Error _ -> return! pollAsync true false lastAdvice
            with
            | :? HttpRequestException as e ->
                setConnectionState ConnectionState.Connecting
                let reconnectDelay = reconnectDelays.GetNext ()
                logger.LogWarning("HTTP request failed. Rehandshaking after  {reconnectDelay}",  reconnectDelay);
                do! Async.Sleep (int reconnectDelay.TotalMilliseconds)
                return! pollAsync transportClosed true lastAdvice
            | :? BayeuxTransportException as e ->
                setConnectionState ConnectionState.Connecting
                let reconnectDelay = reconnectDelays.GetNext ()
                logger.LogWarning("Request transport failed. Retrying after  {reconnectDelay}",  e)
                do! Async.Sleep (int reconnectDelay.TotalMilliseconds)
                return! pollAsync e.IsTransportClosed true lastAdvice
            | :? BayeuxRequestException as e ->
                setConnectionState ConnectionState.Connecting
                logger.LogError("Bayeux request failed with error: {BayeuxError}", e.Message)
                return! pollAsync false true lastAdvice
        }

    member private this.StartLoopPollingAsync advice =
        async {
            try
                let pollAsyncWorkflow = pollAsync this.pollCancel.Token this.HandshakeAsync this.ConnectAsync this.SetConnectionState false false advice
                Async.RunSynchronously (pollAsyncWorkflow, cancellationToken = this.pollCancel.Token)

                this.SetConnectionState ConnectionState.Disconnected
                logger.LogInformation("Long-polling stopped.")
            with
            | :? OperationCanceledException ->
                this.SetConnectionState ConnectionState.Disconnected
                logger.LogInformation("Long-polling stopped.")
            | ex ->
                logger.LogError(ex, "Long-polling stopped on unexpected exception.")
                this.SetConnectionState ConnectionState.DisconnectedOnError
                return reraiseAsync ex // unobserved exception
        }

    member this.StopAsync () =
        Async.StartAsTask(async {
            this.pollCancel.Cancel()
            let! response = this.DisconnectAsync ()
            Interlocked.Exchange(&this.clientId, None) |> ignore
            do! transport.CloseAsync ()
            return response
        })

    member this.StartAsync cancellationToken =
        Async.StartAsTask(async {
            try
                lock pollCancelLock (fun () ->
                    match this.pollCancel with
                    | null -> this.pollCancel <- new CancellationTokenSource ()
                    | _ -> invalidOp "Already started.")

                do! transport.OpenAsync Async.DefaultCancellationToken
                let! advice = this.HandshakeAsync { Reconnect = String.Empty; Interval = 0 }

                // A way to test the re-handshake with a real server is to put some delay here,  between the first handshake response,
                // and the first try to connect. That will cause an "Invalid client id" response, with an advice of reconnect=handshake.
                // This can also be tested with a fake server in unit tests.

                this.StartLoopPollingAsync advice |> Async.Start
            with
            | ex ->
                lock pollCancelLock (fun () ->
                    match this.pollCancel with
                    | null -> ()
                    | _ -> this.pollCancel.Dispose()
                           this.pollCancel <- Unchecked.defaultof<_>)
                return reraiseAsync ex
       }, cancellationToken = cancellationToken) :> Task
