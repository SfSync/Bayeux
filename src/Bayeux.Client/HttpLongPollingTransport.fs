namespace SfSync.Bayeux.Client

open System
open System.IO
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Newtonsoft.Json
open Newtonsoft.Json.Linq

type internal HttpLongPollingTransport (httpClientFactory : Func<HttpClient>, uri : Uri, eventsObserver : IObserver<JObject>, logger : ILogger) =

    let httpClient = httpClientFactory.Invoke ()
    let requestJsonConverter = lazy RequestJsonConverter()

    member this.Dispose () =
        GC.SuppressFinalize this
        httpClient.Dispose ()
    member __.OpenAsync (cancellationToken : CancellationToken) = Task.CompletedTask
    member __.CloseAsync () = Task.CompletedTask

    member __.RequestAsync (requests : obj seq, cancellationToken) =
        Async.StartAsTask(async {
            let messageStr = JsonConvert.SerializeObject (requests, requestJsonConverter.Force())
            logger.LogDebug ("Posting: {messageStr}", messageStr);

            use content = new StringContent(messageStr, Encoding.UTF8, "application/json");
            use postMessage =
                new HttpRequestMessage (HttpMethod.Post, uri, Content = content)
            use! httpResponse =
                httpClient.SendAsync (
                    postMessage,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)

            if logger.IsEnabled LogLevel.Debug && not httpResponse.IsSuccessStatusCode then
                let! responseStr = httpResponse.Content.ReadAsStringAsync ()
                logger.LogDebug ("Received: {responseStr}", responseStr)

            httpResponse.EnsureSuccessStatusCode() |> ignore
            use! stream = httpResponse.Content.ReadAsStreamAsync ()
            let responseToken =
                stream
                |> StreamReader
                |> JsonTextReader
                |> JToken.ReadFrom
            logger.LogDebug("Received: {responseToken}",
                            responseToken.ToString(Formatting.None));

            let tokens = match responseToken with
                         | :? JArray -> responseToken :> JToken seq
                         | _ -> seq { yield responseToken }

            // https://docs.cometd.org/current/reference/#_delivery
            // Event messages MAY be sent to the client in the same HTTP response
            // as any other message other than a /meta/handshake response.
            let response =
                tokens
                |> Seq.fold (fun response token ->
                    let message = token :?> JObject
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

    interface IBayeuxTransport with
        member this.OpenAsync cancellationToken = this.OpenAsync cancellationToken
        member this.RequestAsync (requests, cancellationToken) = this.RequestAsync (requests, cancellationToken)
        member this.CloseAsync () = this.CloseAsync ()
    interface IDisposable with
        member this.Dispose () = this.Dispose ()