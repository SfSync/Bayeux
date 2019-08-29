module Sfsync.Bayeux.Client.Diagnostics

open System.Diagnostics

[<Struct>]
type DiagnosticsState = {
    Activity : Activity voption
    Context : obj
}
with
    member this.StartActivity (source : DiagnosticSource) activityName =
        if ValueOption.isSome this.Activity
            then invalidOp "Use yield to stop previous activity"
        let activity =
            if source.IsEnabled (activityName) then
                let activity = Activity (activityName)
                let eventName = activityName + ".Event"
                if source.IsEnabled (eventName)
                then source.StartActivity (activity, this.Context)
                else activity.Start()
                |> ValueSome
            else ValueNone
        let state = this
        { state with Activity = activity; Context = Unchecked.defaultof<_> }

    member this.StopActivity (source : DiagnosticSource) =
        match this.Activity with
        | ValueSome activity ->
            source.StopActivity (activity, this.Context)
        | ValueNone -> ()

type DiagnosticsBuilder (sourceName) =

    let source = new DiagnosticListener (sourceName) :> DiagnosticSource

    //member __.Zero () =
    //    { Activity = ValueNone; Context = ValueNone }

    member __.Yield (_) =
        { Activity = ValueNone; Context = ValueNone }

    //member __.Combine (state1 : DiagnosticsState, state2 : DiagnosticsState) =
    //    state1.StopActivity source
    //    state2

    //member __.Delay (f) = f()

    member __.For (state, action : _ -> DiagnosticsState) =
        action state

    [<CustomOperation("activity")>]
    member __.Activity (state : DiagnosticsState, activityName) =
        state.StartActivity source activityName

    [<CustomOperation("context")>]
    member __.Context<'Context> (state : DiagnosticsState, context : 'Context) =
        { state with Context = context :> obj }

    [<CustomOperation("event")>]
    member __.Event (state : DiagnosticsState, eventName) =
        if source.IsEnabled (eventName) then
            source.Write(eventName, state.Context)
        state

    member __.Run (state : DiagnosticsState) = state.StopActivity source

let diagnostics = DiagnosticsBuilder "Sfsync.Bayeux.Client";;

diagnostics {
    context "5"
    printf "fdf"
    activity "ace"
    context 5
}

