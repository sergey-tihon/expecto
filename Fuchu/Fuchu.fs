﻿namespace Fuchu

open System
open System.Linq
open System.Runtime.CompilerServices

type TestCode = unit -> Choice<unit, string>

type Test = 
    | TestCase of TestCode
    | TestList of Test list
    | TestLabel of string * Test

[<AutoOpen>]
module F =

    let withLabel label test = TestLabel (label, test)

    type TestResult = 
        | Passed
        | Failed of string
        | Exception of exn

    let testResultToString =
        function
        | Passed -> "Passed"
        | Failed error -> "Failed: " + error
        | Exception e -> "Exception: " + e.ToString()

    type TestResultCounts = {
        Passed: int
        Failed: int
        Errored: int
    }
        with override x.ToString() =
                        sprintf "%d tests run: %d passed, %d failed, %d errored\n"
                            (x.Errored + x.Failed + x.Passed)
                            x.Passed
                            x.Failed
                            x.Errored

    let testResultCountsToErrorLevel (c: TestResultCounts) =
        (if c.Failed > 0 then 1 else 0) ||| (if c.Errored > 0 then 2 else 0)

    let sumTestResults results =
        let counts = 
            results 
            |> Seq.map snd
            |> Seq.countBy (function
                            | Passed -> 0
                            | Failed _ -> 1
                            | Exception _ -> 2)
            |> dict
        let get i = 
            match counts.TryGetValue i with
            | true, v -> v
            | _ -> 0

        { Passed = get 0
          Failed = get 1
          Errored = get 2 }

    [<CompiledName("Ok")>]
    let ok : Choice<unit, string> = Choice1Of2 ()

    [<CompiledName("Fail")>]
    let fail (msg: string) : Choice<unit, string> = Choice2Of2 msg
    let failf fmt = Printf.ksprintf Choice2Of2 fmt

    [<CompiledName("AssertEqual")>]
    let assertEqual expected actual = 
        if actual = expected
            then ok
            else failf "Expected %A but was %A" expected actual

    [<CompiledName("AssertTrue")>]
    let assertTrue x = assertEqual true x

    [<CompiledName("AssertFalse")>]
    let assertFalse x = assertEqual false x

    [<CompiledName("AssertThrows")>]
    let assertThrows ex f = 
        try
            f()
            ok
        with e -> assertEqual ex (e.GetType())

    let flatten =
        let rec loop parentName testList =
            function
            | TestLabel (name, test) -> 
                loop (parentName + "/" + name) testList test
            | TestCase test -> (parentName, test)::testList
            | TestList tests -> List.collect (loop parentName testList) tests
        loop "" []

    let eval beforeRun onPassed onFailed onException map =
        let execOne (name: string, test) = 
            try
                beforeRun name
                match test() with
                | Choice1Of2() -> 
                    let r = name, Passed
                    onPassed name
                    r
                | Choice2Of2 error -> 
                    let r = name, Failed error
                    onFailed name error
                    r
            with e -> 
                let r = name, Exception e
                onException name e
                r                        
        map execOne

    let flattenEval beforeRun onPassed onFailed onException map tests =
        flatten tests |> eval beforeRun onPassed onFailed onException map

    let printPassed = printfn "%s: Passed"
    let printFailed = printfn "%s: Failed: %s"
    let printException = printfn "%s: Exception: %A"

    [<Extension>]
    [<CompiledName("Run")>]
    let run tests = 
        let results = flattenEval ignore printPassed printFailed printException Seq.map tests
        let summary = sumTestResults results
        Console.WriteLine summary
        testResultCountsToErrorLevel summary

    let pmap (f: _ -> _) (s: _ seq) = s.AsParallel().Select f

    [<Extension>]
    [<CompiledName("RunParallel")>]
    let runParallel tests = 
        let locker = obj()
        let printPassed name = 
            lock locker (fun () -> printPassed name)
        let printFailed name error =
            lock locker (fun () -> printFailed name error)
        let printException name ex =
            lock locker (fun () -> printException name ex)
        let results = flattenEval ignore printPassed printFailed printException pmap tests
        let summary = sumTestResults results
        Console.WriteLine summary
        testResultCountsToErrorLevel summary

open System.Reflection

[<Extension>]
type Test with
    static member NewCase (f: Func<Choice<unit, string>>) = 
        TestCase f.Invoke

    static member NewList ([<ParamArray>] tests) = 
        Array.toList tests |> TestList

    static member NewList ([<ParamArray>] tests) =
        tests |> Array.map Test.NewCase |> Test.NewList

    [<Extension>]
    static member WithLabel (test, label) = TestLabel (label, test)

    [<Extension>]
    static member Add (test, add) = TestList [test; TestCase add]

    [<Extension>]
    static member Add (test, add) = TestList [test; add]

    static member FromMember (m: MemberInfo) =
        let toFunc (m: MethodInfo) = Func<Choice<unit, string>>(fun () -> unbox (m.Invoke(null, [||])))
        [m]
        |> Seq.filter (fun m -> m.MemberType = MemberTypes.Method)
        |> Seq.map (fun m -> m :?> MethodInfo)
        |> Seq.filter (fun m -> m.ReturnType = typeof<Choice<unit, string>> && m.GetParameters().Length = 0)
        |> Seq.map (fun m -> m.Name, toFunc m)
        |> Seq.map (fun (name, code) -> Test.NewCase code |> withLabel name)
        |> Seq.toList
        |> TestList

    static member FromType (t: Type) =
        t.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
        |> Seq.map Test.FromMember
        |> Seq.toList
        |> TestList
        |> withLabel t.Name

    static member FromAssembly (a: Assembly) =
        a.GetExportedTypes()
        |> Seq.map Test.FromType
        |> Seq.toList
        |> TestList
        |> withLabel (a.FullName.Split ',').[0]

        