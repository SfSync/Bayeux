namespace Sfsync.Bayeux.Client

open System
open System.Collections.Generic
open System.Runtime.InteropServices

[<Struct>]
type internal ReconnectState =
    | Succeeded
    | NotSucceeded

[<Struct>]
type internal DelayInfo =
    {
        State : ReconnectState
        CurrentDelay : TimeSpan
        Enumerator : IEnumerator<TimeSpan>
    }

type ReconnectDelays ([<Optional;DefaultParameterValue(null)>] ?delays0 : TimeSpan seq) =

    let getDefaultDelays () = seq { yield TimeSpan.Zero; yield TimeSpan.FromSeconds(5.0) }
    let delays =
        let delays = defaultArg delays0 (getDefaultDelays ())
        if Seq.isEmpty delays
            then (getDefaultDelays ())
            else delays

    let mutable delay =
        { State = Succeeded;
          CurrentDelay = Seq.head delays;
          Enumerator = delays.GetEnumerator () }

    member this.ResetIfLastSucceeded () =
        match delay.State with
        | Succeeded ->
            delay <- { delay with Enumerator = delays.GetEnumerator () }
        | NotSucceeded ->
            delay <- { delay with State = Succeeded }
        ()

    member this.GetNext () =
        delay <- { delay with State = NotSucceeded }
        if delay.Enumerator.MoveNext ()
            then delay <- { delay with CurrentDelay = delay.Enumerator.Current }
        delay.CurrentDelay


