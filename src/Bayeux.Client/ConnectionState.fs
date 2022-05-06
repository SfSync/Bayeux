namespace SfSync.Bayeux.Client

open System

type ConnectionState =
    | Disconnected = 0
    | Connecting = 1
    | Connected = 2
    | DisconnectedOnError = 3

type ConnectionStateChangedArgs (connectionState : ConnectionState) =
    inherit EventArgs ()

    member __.ConnectionState = connectionState
    override __.ToString () = connectionState.ToString ()
