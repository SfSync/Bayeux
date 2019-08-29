namespace Sfsync.Bayeux.Client

open Sfsync.Bayeux.Client.Message
open System
open Newtonsoft.Json

[<Struct>]
type Version = BayeuxVersion of Version : string
[<Struct>]
type ClientId = ClientId of Id : string
[<Struct>]
type Subscription = Subscription of Channel : string
[<Struct>]
type SupportedConnectionTypes = SupportedConnectionTypes of string list

type ConnectionType =
    | LongPolling
    | CallbackPolling
    | WebSocket
    override this.ToString() =
        match this with
        | LongPolling -> "long-polling"
        | CallbackPolling -> "callback-polling"
        | WebSocket -> "websocket"

type Request =
    | Handshake of Version : Version * SupportedConnectionTypes : SupportedConnectionTypes * Replay : bool voption
    | Connect of ClientId : ClientId * ConnectionType : ConnectionType
    | Disconnect of ClientId : ClientId
    | Subscribe of ClientId : ClientId * Subscription : Subscription * ReplayId : int64 voption
    | Unsubscribe of ClientId : ClientId * Subscription : Subscription
    member this.Channel =
        match this with
        | Handshake _ -> ChannelFields.MetaHandshake
        | Connect _ -> ChannelFields.MetaConnect
        | Disconnect _ -> ChannelFields.MetaDisconnect
        | Subscribe _ -> ChannelFields.MetaSubscribe
        | Unsubscribe _ -> ChannelFields.MetaUnsubscribe

// TODO: Try using https://github.com/vsapronov/FSharp.Json instead
type RequestJsonConverter () =
    inherit JsonConverter ()

    let writeBoolProperty (name, value : bool) (writer : JsonWriter) =
        writer.WritePropertyName name
        writer.WriteValue value

    let writeStringProperty (name, value : string) (writer : JsonWriter) =
        writer.WritePropertyName name
        writer.WriteValue value

    let writeInt64Property (name, value : int64) (writer : JsonWriter) =
        writer.WritePropertyName name
        writer.WriteValue value

    let writeArrayProperty (name, values : string seq) (writer : JsonWriter) =
        writer.WritePropertyName name
        writer.WriteStartArray ()
        for value in values do
            writer.WriteValue value
        writer.WriteEndArray ()

    let writeObject (name, writeObjectFunc : JsonWriter -> unit) (writer : JsonWriter) =
        writer.WritePropertyName name
        writer.WriteStartObject ()
        writeObjectFunc writer
        writer.WriteEndObject ()

    override __.WriteJson (writer, value, serializer) =
        let writeProperty' (name, value) = writeStringProperty (name, value) writer
        let writeArrayProperty' (name, values) = writeArrayProperty (name, values) writer
        let writeObject' (name, writeObjectFunc) = writeObject (name, writeObjectFunc) writer
        match value with
        | :? Request as request ->
            writer.WriteStartObject ()
            match request with
            | Handshake (BayeuxVersion version, SupportedConnectionTypes types, replay) ->
                writeProperty' ("channel", request.Channel)
                writeProperty' ("version", version)
                writeArrayProperty' ("supportedConnectionTypes", types |> Seq.ofList)
                match replay with
                | ValueSome replay ->
                    writeObject' (Fields.Ext,
                        writeBoolProperty ("replay", replay))
                | ValueNone -> ()
            | Connect (ClientId clientId, connectionType) ->
                writeProperty' ("clientId", clientId)
                writeProperty' ("channel", request.Channel)
                writeProperty' ("connectionType", connectionType.ToString ())
            | Disconnect (ClientId clientId) ->
                writeProperty' ("clientId", clientId)
                writeProperty' ("channel", request.Channel)
            | Subscribe (ClientId clientId, Subscription subscription, replayId) ->
                writeProperty' ("clientId", clientId)
                writeProperty' ("channel", request.Channel)
                writeProperty' ("subscription", subscription)
                match replayId with
                | ValueSome replayId ->
                    writeObject' (Fields.Ext,
                        writeObject ("replay",
                             writeInt64Property (subscription, replayId)))
                | ValueNone -> ()

            | Unsubscribe (ClientId clientId, Subscription subscription) ->
                writeProperty' ("clientId", clientId)
                writeProperty' ("channel", request.Channel)
                writeProperty' ("subscription", subscription)

            writer.WriteEndObject ()
        | _ -> ()

    override __.ReadJson (reader, objectType, existingValue, serializer) =
        raise (NotImplementedException ())

    override __.CanRead = false

    override __.CanConvert (objectType) =
        typeof<Request>.IsAssignableFrom objectType
