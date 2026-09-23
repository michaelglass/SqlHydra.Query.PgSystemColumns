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
open System
open System.Xml.Linq

let expectedFloor = "5.0.0"

/// BRANCH ONLY. The one prerelease this check will accept, and only when the fsproj
/// declares it exactly.
///
/// The `ext/contribute-columns` seam (`IContributeColumns`, `ColumnContributionContext`,
/// `Column.Doc`) is not in any released SqlHydra, so this branch has to point at a locally
/// packed build. Everything the check exists to prevent still applies -- this pin must not
/// reach a release -- so the exception is one named constant here rather than a relaxed
/// rule: set it back to `None`, and the fsproj back to a stable floor, and the check goes
/// back to refusing every prerelease.
let branchOnlyFloor: string option = Some "5.1.0-seam.2"

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

let declared =
    match sqlHydraQueryReferences with
    | [ reference ] ->
        match attributeValue "Version" reference with
        | Some version -> version
        | None -> failwith "The SqlHydra.Query PackageReference declares no Version attribute."
    | [] -> failwith "No PackageReference to SqlHydra.Query found in the project."
    | many -> failwith $"Expected one PackageReference to SqlHydra.Query, found {List.length many}."

/// The lower bound of a NuGet version string. A bare version is its own floor;
/// a range such as `[4.1.1, 5.0.0)` floors at its first component.
let floorOf (version: string) =
    let trimmed = version.Trim()

    if trimmed.StartsWith "[" || trimmed.StartsWith "(" then
        trimmed.TrimStart('[', '(').Split(',').[0].Trim()
    else
        trimmed

let floor = floorOf declared

let fail (reason: string) =
    eprintfn $"verify-package-metadata: {reason}"
    eprintfn $"  declared: SqlHydra.Query Version=\"{declared}\" (floor {floor})"
    eprintfn $"  expected: the stable host release {expectedFloor}"
    exit 1

match branchOnlyFloor with
| Some allowed when floor = allowed ->
    printfn $"verify-package-metadata: SqlHydra.Query floor is the BRANCH-ONLY prerelease {floor}."
    printfn "  This must not be released. Put the floor back to a stable version, and branchOnlyFloor back to None."
    exit 0
| _ -> ()

if floor.Contains "-" then
    fail "the SqlHydra.Query floor is a PRERELEASE; a prerelease floor drags every consumer onto a prerelease host."

let parse (v: string) =
    match Version.TryParse v with
    | true, parsed -> parsed
    | false, _ -> fail $"the SqlHydra.Query floor '{v}' is not a parseable version."

let floorVersion = parse floor
let expectedVersion = parse expectedFloor

if floorVersion < expectedVersion then
    fail "the SqlHydra.Query floor is BELOW the stable host release this package is built against."

if floorVersion > expectedVersion then
    fail
        "the SqlHydra.Query floor was raised without updating expectedFloor in this script; raising a floor is breaking, do it deliberately in both places."

printfn $"SqlHydra.Query dependency floor is {floor} (stable, as expected)."
