using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.FSharp.Core;
using Newtonsoft.Json.Linq;
using Sfsync.Bayeux.Client;

namespace SfSync.Bayeux.Sample
{
    class Program
    {
        static void OnNext(JObject message) => Console.WriteLine(message);
        static void OnError(Exception exception) => Console.WriteLine(exception);

        static async Task Main(string[] args)
        {
            var loggerFactory = new LoggerFactory();
            var streamingUri = new Uri("<uri>");
            var observer = Observer.Create<JObject>(OnNext, OnError);
            var clientOptions = new HttpLongPollingTransportOptions(() => new HttpClient(),
      streamingUri, observer);
            var connection = new BayeuxConnection(clientOptions, new ReconnectDelays(), loggerFactory);
            var cancellationTokenSource = new CancellationTokenSource();
            await connection.StartAsync(cancellationTokenSource.Token);
            var channels = new (string, FSharpValueOption<long>)[]
            {
                ("<channel path>", FSharpValueOption<long>.None)
            };
            var result = await connection.SubscribeAsync(channels, cancellationTokenSource.Token);

            Console.ReadKey();
        }
    }
}
