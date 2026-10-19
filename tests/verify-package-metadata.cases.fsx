// Drives verify-package-metadata.fsx through a table of declared SqlHydra.Query
// versions and asserts each exit code. A valid NuGet range whose inclusive floor
// is the stable host release passes; an exclusive or prerelease floor fails.
open System
open System.Diagnostics

let script = IO.Path.Combine(__SOURCE_DIRECTORY__, "verify-package-metadata.fsx")

let cases =
    [ "5.1.1", 0
      "[5.1.1]", 0
      "[5.1.1, 6.0.0)", 0
      "(5.1.1,)", 1
      "5.1.1-beta.1", 1
      "5.1.0", 1 ]

let run (declared: string) =
    let info = ProcessStartInfo("dotnet")

    for arg in [ "fsi"; script; "--declared"; declared ] do
        info.ArgumentList.Add arg

    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    use proc = Process.Start info
    let stdout = proc.StandardOutput.ReadToEndAsync()
    let stderr = proc.StandardError.ReadToEndAsync()
    proc.WaitForExit()
    proc.ExitCode, stdout.Result + stderr.Result

let failures =
    cases
    |> List.filter (fun (declared, expected) ->
        let actual, output = run declared
        let ok = actual = expected
        printfn "%s  %-18s expected exit %d, got %d" (if ok then "ok  " else "FAIL") declared expected actual

        if not ok then
            eprintfn "%s" output

        not ok)

if not failures.IsEmpty then
    eprintfn $"verify-package-metadata.cases: {failures.Length} of {cases.Length} cases failed."
    exit 1

printfn $"verify-package-metadata.cases: all {cases.Length} cases passed."
