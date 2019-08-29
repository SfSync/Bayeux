namespace Sfsync.Bayeux.Client

/// <summary>
/// <p>The Bayeux protocol exchange information by means of messages.</p>
/// <p>This interface represents the API of a Bayeux message, and consists
/// mainly of convenience methods to access the known fields of the message map.</p>
/// <p>This interface comes in both an immutable and {@link Mutable mutable} versions.<br/>
/// Mutability may be deeply enforced by an implementation, so that it is not correct
/// to cast a passed Message, to a Message.Mutable, even if the implementation
/// allows this.</p>
/// </summary>
module Message = begin

    module Fields =
        let [<Literal>] ClientId = "clientId"
        let [<Literal>] Data = "data"
        let [<Literal>] Channel = "channel"
        let [<Literal>] Id = "id"
        let [<Literal>] Error = "error"
        let [<Literal>] Timestamp = "timestamp"
        let [<Literal>] Transport = "transport"
        let [<Literal>] Advice = "advice"
        let [<Literal>] Successful = "successful"
        let [<Literal>] Subscription = "subscription"
        let [<Literal>] Ext = "ext"
        let [<Literal>] ConnectionType = "connectionType"
        let [<Literal>] Version = "version"
        let [<Literal>] MinVersion = "minimumVersion"
        let [<Literal>] SupportedConnectionTypes = "suppalortedConnectionTypes"
        let [<Literal>] Reconnect = "reconnect"
        let [<Literal>] Interval = "interval"

    module ReconnectValues =
        let [<Literal>] Retry = "retry"
        let [<Literal>] Handshake = "handshake"
        let [<Literal>] None = "none"

    module EventTypes =
        let [<Literal>] Created = "created"
        let [<Literal>] Updated = "updated"
        let [<Literal>] Deleted = "deleted"
        let [<Literal>] Undeleted = "undeleted"

    module ExtFields =
        let [<Literal>] Replay = "replay"

end

open Newtonsoft.Json

[<CLIMutable>]
[<Struct>]
type internal BayeuxAdvice =
    {
        [<JsonProperty(Message.Fields.Reconnect)>]
        Reconnect : string
        [<JsonProperty(Message.Fields.Interval)>]
        Interval : int
    }

[<CLIMutable>]
[<Struct>]
type internal BayeuxExt =
    {
        [<JsonProperty(Message.ExtFields.Replay)>]
        Replay : bool
    }
