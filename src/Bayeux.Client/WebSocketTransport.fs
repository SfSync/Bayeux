namespace SfSync.Bayeux.Client

open FSharp.Collections.ParallelSeq
open System
open System.Buffers
open System.Collections.Concurrent
open System.IO
open System.Linq
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Newtonsoft.Json
open Newtonsoft.Json.Linq

module MessageFields = Message.Fields

type internal WebSocketTransport (webSocketFactory : Func<WebSocket> voption, uri : Uri, responseTimeout : TimeSpan, eventsObserver : IObserver<JObject>, logger : ILogger) =

    let responseTimeout =
        if responseTimeout = TimeSpan.Zero
        then TimeSpan.FromSeconds(65.)
        else responseTimeout

    [<DefaultValue>]
    val mutable webSocket : WebSocket
    [<DefaultValue>]
    val mutable receiverLoopTask : Task
    let mutable receiverLoopCancel : CancellationTokenSource voption = ValueNone

    let pendingRequests = ConcurrentDictionary<string, TaskCompletionSource<JObject>>()
    let mutable nextMessageId = 0L

    member private this.ClearPendingRequests (fault : Exception voption) =
        match fault with
        | ValueSome e ->
            pendingRequests
            |> PSeq.iter (fun r -> r.Value.SetException e)
        | ValueNone ->
            pendingRequests
            |> PSeq.iter (fun r -> r.Value.SetCanceled ())

        pendingRequests.Clear ()

    member private this.ReceiveMessageAsync () =
        async {
            let arrayPool = ArrayPool<byte>.Shared;
            let stream = new MemoryStream()
            let bufferArray = arrayPool.Rent(8192)
            try
                let buffer = new ArraySegment<byte>(bufferArray)

                let rec receiveAsync () = async {
                    let! result = this.webSocket.ReceiveAsync (buffer, Async.DefaultCancellationToken)
                    stream.Write(buffer.Array, buffer.Offset, result.Count)
                    if result.EndOfMessage
                    then return ()
                    else return! receiveAsync ()
                }

                do! receiveAsync ()

                stream.Seek (0L, SeekOrigin.Begin) |> ignore
            finally
                arrayPool.Return(bufferArray)

            return stream;
        }

    member internal this.HandleReceivedMessageAsync (stream : Stream) =
        async {
            use reader = new StreamReader(stream, Encoding.UTF8)
                         |> JsonTextReader
            let received = JToken.ReadFrom(reader)
            logger.LogDebug ("Received: {received}", received.ToString(Formatting.None))

            let responses =
                match received with
                | :? JObject as jObject -> seq { yield jObject }
                | :? JArray as jArray -> jArray.Children().Cast<JObject>()
                | _ -> raise (InvalidDataException ()) // TODO: Improve

            let events = ResizeArray<JObject>()
            for response in responses do
                match response.[MessageFields.Id] with
                | null -> events.Add response
                | messageId ->
                    let found, requestTask = pendingRequests.TryRemove (messageId.ToString ())
                    if found
                        then requestTask.SetResult(response)
                        else logger.LogError("Request not found for received responsewithid'{messageId}'", messageId)

            events
            |> Seq.iter eventsObserver.OnNext
        }

    member private this.StartReceiverLoopAsync (cancellationToken : CancellationToken) =
        Async.StartAsTask (async {
            let! fault = async {
                try
                    while not (cancellationToken.IsCancellationRequested) do
                        let! message = this.ReceiveMessageAsync ()
                        do! this.HandleReceivedMessageAsync message

                    return ValueNone
                with
                | :? OperationCanceledException -> return ValueNone
                | :? WebSocketException as e ->
                    // It is not possible to infer whether the webSocket is closed from     webSocket.State,
                    // and not clear how to infer it from WebSocketException. So we always assume   that it     is closed.
                    return ValueSome (BayeuxTransportException ("WebSocket receive message failed. Connection assumed closed.", e, isTransportClosed = true) :> Exception)
                | e ->
                    logger.LogError(e, "Unexpected exception thrown in WebSocket receiving loop")
                    return ValueSome (BayeuxTransportException ("Unexpected exception. Connection assumed closed.", e, isTransportClosed = true) :> Exception)
            }
            this.ClearPendingRequests (fault)
        })

    member this.OpenAsync (cancellationToken : CancellationToken) =
        Async.StartAsTask (async {
            match receiverLoopCancel with
            | ValueSome cts ->
                cts.Cancel();
                do! this.receiverLoopTask
            | ValueNone -> ()

            this.webSocket <-
                match webSocketFactory with
                | ValueSome webSocketFactory -> webSocketFactory.Invoke ()
                | ValueNone -> SystemClientWebSocket.CreateClientWebSocket ()

            try
                do! this.webSocket.ConnectAsync (uri, Async.DefaultCancellationToken)
            with
            | e ->
                raise (BayeuxTransportException ("WebSocket connect failed.", e, isTransportClosed = true))

            let cts = new CancellationTokenSource ()
            receiverLoopCancel <- ValueSome cts
            this.receiverLoopTask <- this.StartReceiverLoopAsync cts.Token
        }, cancellationToken = cancellationToken) :> Task

    member this.SendAsync (message : string, cancellationToken) =
        Async.StartAsTask (async {
            let bytes = ArraySegment<byte>(Encoding.UTF8.GetBytes(message))
            try
                do! this.webSocket.SendAsync(
                    bytes,
                    WebSocketMessageType.Text,
                    endOfMessage = true,
                    cancellationToken = Async.DefaultCancellationToken)
            with
            | e ->
                raise (BayeuxTransportException ("WebSocket send failed.", e,     isTransportClosed = (this.webSocket.State <> WebSocketState.Open)))
        }, cancellationToken = cancellationToken) :> Task

    member this.RequestAsync (requests : obj seq, cancellationToken) =
        Async.StartAsTask (async {
            let responseTasks = ResizeArray<TaskCompletionSource<JObject>> ()
            let requestsJArray = JArray.FromObject requests
            let messageIds = ResizeArray<string> ()
            for request in requestsJArray do
                let messageId = Interlocked.Increment(&nextMessageId).ToString ()
                request.[MessageFields.Id] <- JToken.op_Implicit messageId
                messageIds.Add(messageId)

                let responseReceived = TaskCompletionSource<JObject>()
                pendingRequests.TryAdd (messageId, responseReceived) |> ignore
                responseTasks.Add(responseReceived)

            let messageStr = JsonConvert.SerializeObject requestsJArray
            logger.LogDebug ("Posting: {messageStr}", messageStr)
            do! this.SendAsync (messageStr, Async.DefaultCancellationToken)

            let timeoutTask = Task.Delay (responseTimeout, Async.DefaultCancellationToken)
            let! completedTask =
                Task.WhenAny (Task.WhenAll(responseTasks.Select(fun t -> t.Task)),
                              timeoutTask)

            for id in messageIds do
                pendingRequests.TryRemove id |> ignore

            if completedTask = timeoutTask
            then
                return raise (TimeoutException ())
            else
                let response =
                    responseTasks
                    |> Seq.map (fun tcs -> tcs.Task.Result)
                    |> Seq.fold (fun response token ->
                        let message = token
                        match message.["channel"] with
                        // TODO: Handle no channel case
                        | null -> "No 'channel' field in message."
                                  |> BayeuxProtocolException
                                  |> eventsObserver.OnError
                                  response
                        | channel when channel.Value<string>().StartsWith ChannelFields.Meta
                            -> message
                        | _ -> eventsObserver.OnNext message
                               response)
                        Unchecked.defaultof<JObject>

                return response
        }, cancellationToken = cancellationToken)

    member this.CloseAsync () =
        Async.StartAsTask (async {
            this.ClearPendingRequests ValueNone

            match receiverLoopCancel with
            | ValueSome cts ->
                cts.Cancel()
                receiverLoopCancel <- ValueNone
            | ValueNone -> ()

            match this.webSocket with
            | null -> ()
            | _ ->
                do! this.webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, String.Empty, Async.DefaultCancellationToken)
                this.webSocket.Dispose()
                this.webSocket <- null
        }) :> Task

    member this.Dispose () =
        GC.SuppressFinalize this
        this.CloseAsync().Wait()

    interface IBayeuxTransport with
        member this.OpenAsync cancellationToken = this.OpenAsync cancellationToken
        member this.RequestAsync (requests, cancellationToken) = this.RequestAsync (requests, cancellationToken)
        member this.CloseAsync () = this.CloseAsync ()
    interface IDisposable with
        member this.Dispose () = this.Dispose ()
