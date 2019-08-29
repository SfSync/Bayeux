# .NET Bayeux Client written in F#

Rewrite of https://github.com/ErnestoGarciaGenesys/bayeux-client-dotnet/ in F# with simplified API
Help in porting test is highly appreciated.

[Bayeux](https://docs.cometd.org/current/reference/#_bayeux) is a protocol that enables web-friendly message transport over HTTP, WebSockets, etc.

This library provides a client for receiving events from a Bayeux server, through the HTTP long-polling transport, or WebSockets. It provides convenient async methods.

## Quick start

This is an example of usage:

F#
``` F#
open System
open System.Net.Http
open System.Reactive
open System.Threading
open Microsoft.Extensions.Logging
open Newtonsoft.Json.Linq
open Sfsync.Bayeux.Client

let streamingUri = Uri "<uri>"
let onNext (message : JObject) = ()
let onError (error : exn) = ()
let observer = Observer.Create (onNext, onError)
let clientOptions =
    { HttpLongPollingTransportOptions.HttpClientFactory = fun () -> new HttpClient()
      Uri = streamingUri
      EventsObserver = observer }
let loggerFactory = new LoggerFactory()
let connection = new BayeuxConnection (clientOptions, ReconnectDelays (), loggerFactory)

let startAsync (channels, cancellationToken) =
    async {
        do! connection.StartAsync cancellationToken
        return! connection.SubscribeAsync (channels, cancellationToken)
    }

```

C#
``` C#
static void OnNext(JObject message) => Console.WriteLine(message);
static void OnError(Exception exception) => Console.WriteLine(exception);

static async Task Main(string[] args)
{
    var loggerFactory = new LoggerFactory();
    var streamingUri = new Uri("<uri>");
    var observer = Observer.Create<JObject>(OnNext, OnError);
    var clientOptions =
        new HttpLongPollingTransportOptions(
            () => new HttpClient(),
            streamingUri,
            observer);
    var connection = new BayeuxConnection(clientOptions, new ReconnectDelays(), loggerFactory);

    var channels = new (string, FSharpValueOption<long>)[]
    {
        ("<channel path>", FSharpValueOption<long>.None)
    };
    var cancellationTokenSource = new CancellationTokenSource();
    await connection.StartAsync(cancellationTokenSource.Token);
    var result = await connection.SubscribeAsync(channels, cancellationTokenSource.Token);

    Console.ReadKey();
}
```

## Logging

Project contains `DiagnosticsBuilder.fs` which uses `System.Diagnostics.Activity` from `System.Diagnostics.DiagnosticSource`
I Implemented it but when I tested library broke and did not receive any events

For troubleshooting networking issues, you may want to enable [.NET network tracing](https://docs.microsoft.com/en-us/dotnet/framework/network-programming/how-to-configure-network-tracing).

## Features

### Connection and subscription recovery

Reconnections and automatic channel re-subscriptions are implemented.

Reconnection delays can be provided as a parameter of the `BayeuxConnection` constructors.

### Subscription

You can start and stop receiving events by calling `StartAsync` and `StopAsync`
`SubscribeAsync` and `UnsubscribeAsync` adds and removes channels to listen to

### WebSockets

WebSocket transport is supported:

``` C#
var loggerFactory = new LoggerFactory();
var bayeuxConnection = new BayeuxConnection(
    new WebSocketTransportOptions(
        FsharpValueOption<Func<WebSocket>>.None
        new Uri("ws://localhost:8080/bayeux/"),
        loggerFactory);
```

### Bayeux protocol extensions

Currently on **replay id** extension is implemented
