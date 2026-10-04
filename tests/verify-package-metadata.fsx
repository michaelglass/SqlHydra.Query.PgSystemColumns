// Asserts the SqlHydra.Query dependency floor is the STABLE host release.
//
// A PackageReference version is a floor: NuGet resolves a consumer's transitive
// graph to the lowest version that satisfies every constraint, so whatever this
// project names is what every consumer is dragged onto. A prerelease floor
// drags them onto a prerelease of the host library; a floor below the stable
// release names an API surface this package was not built against. Both are
// one routine bump away, and a floor that is only declared in a comment decays
// at the first such bump — which is how a prerelease floor survived four
// releases of the pgvector sibling. This script runs as its own CI job and in
// the local `check`/`ci` tasks, and exits non-zero on any deviation.
//
// Raising the floor is a deliberate, breaking act (whoever pinned to the old
// floor can no longer resolve), so it is done here, on purpose, not by editing
// the fsproj alone.
#r "nuget: NuGet.Versioning, 6.12.4"

open System
open System.Xml.Linq
open NuGet.Versioning

let expectedFloor = "5.1.0"

let projectPath =
    IO.Path.Combine(
        __SOURCE_DIRECTORY__,
        "..",
        "src",
        "SqlHydra.Query.PgSystemColumns",
        "SqlHydra.Query.PgSystemColumns.fsproj"
    )

let project = XDocument.Load projectPath

let attributeValue (name: string) (element: XElement) =
    match element.Attribute(XName.Get name) with
    | null -> None
    | attribute -> Some attribute.Value

let sqlHydraQueryReferences =
    project.Descendants(XName.Get "PackageReference")
    |> Seq.filter (fun reference -> attributeValue "Include" reference = Some "SqlHydra.Query")
    |> List.ofSeq

/// `--declared <range>` checks that value instead of the fsproj's, so
/// verify-package-metadata.cases.fsx can drive this script through a table of
/// declared versions and their expected exit codes.
let declaredOverride =
    match fsi.CommandLineArgs |> List.ofArray |> List.skip 1 with
    | [ "--declared"; value ] -> Some value
    | [] -> None
    | other -> failwith $"Unexpected arguments: %A{other}. Usage: [--declared <version-range>]"

let declared =
    match declaredOverride with
    | Some value -> value
    | None ->
        match sqlHydraQueryReferences with
        | [ reference ] ->
            match attributeValue "Version" reference with
            | Some version -> version
            | None -> failwith "The SqlHydra.Query PackageReference declares no Version attribute."
        | [] -> failwith "No PackageReference to SqlHydra.Query found in the project."
        | many -> failwith $"Expected one PackageReference to SqlHydra.Query, found {List.length many}."

let fail (reason: string) =
    eprintfn $"verify-package-metadata: {reason}"
    eprintfn $"  declared: SqlHydra.Query Version=\"{declared}\""
    eprintfn $"  expected: an inclusive floor at the stable host release {expectedFloor}"
    exit 1

/// The declared value parsed the way NuGet parses it: a bare version `5.1.0` is
/// the range `[5.1.0, )`, and `[5.1.0]`, `[5.1.0, 6.0.0)` and `(5.1.0,)` are
/// ranges whose floor is `MinVersion`.
let range =
    match VersionRange.TryParse declared with
    | true, parsed -> parsed
    | false, _ -> fail $"'{declared}' is not a valid NuGet version range."

let floor =
    match range.MinVersion with
    | null -> fail "the range has no lower bound, so NuGet may resolve any SqlHydra.Query version."
    | _ when not range.IsMinInclusive ->
        fail "the lower bound is EXCLUSIVE, so the floor version itself is not allowed."
    | minVersion -> minVersion

if floor.IsPrerelease then
    fail "the SqlHydra.Query floor is a PRERELEASE; a prerelease floor drags every consumer onto a prerelease host."

let expectedVersion = NuGetVersion.Parse expectedFloor

if floor < expectedVersion then
    fail "the SqlHydra.Query floor is BELOW the stable host release this package is built against."

if floor > expectedVersion then
    fail
        "the SqlHydra.Query floor was raised without updating expectedFloor in this script; raising a floor is breaking, do it deliberately in both places."

printfn $"SqlHydra.Query dependency floor is {floor} from '{declared}' (stable, as expected)."
