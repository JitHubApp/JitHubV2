# Self-test for Test-SourceOrganization.ps1: a conforming temporary tree passes,
# and each rule fails on a tree that breaks only that rule.
#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Test-SourceOrganization.ps1'))
$pwsh = (Get-Process -Id $PID).Path
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "jithub-source-organization-$([guid]::NewGuid().ToString('N'))"
$exceptionsFile = 'eng/source-organization-exceptions.json'

function Write-TreeFile([string] $Tree, [string] $RelativePath, [string] $Content) {
    $path = Join-Path $Tree $RelativePath
    [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Content)
}

function Write-Exceptions([string] $Tree, [object[]] $Exceptions) {
    Write-TreeFile $Tree $exceptionsFile (@{ exceptions = $Exceptions } | ConvertTo-Json -Depth 4)
}

$legacyException = @{ code = 'SRC0002'; path = 'App/Legacy/OldName.cs'; reason = 'Self-test exception.' }

# A tree that follows every rule: an app with top-level statements, XAML
# code-behind and a nested project; a library that tracks its public API, with
# a partial type whose accessibility one part declares; namespaces and type
# declarations inside comments and literals, which must not count; a block
# namespace; a listed exception; and violations only where the check does not look.
function New-ConformingTree([string] $Name) {
    $tree = Join-Path $tempRoot $Name
    Write-TreeFile $tree 'App/App.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>'
    Write-TreeFile $tree 'App/Program.cs' @'
using App.Services;

var store = new Store();
System.Console.WriteLine(store.Name);

internal sealed partial class Program
{
}
'@
    Write-TreeFile $tree 'App/GlobalUsings.cs' 'global using System;'
    Write-TreeFile $tree 'App/App.xaml' '<Application />'
    Write-TreeFile $tree 'App/App.xaml.cs' "namespace App;`npublic sealed partial class App { }"
    Write-TreeFile $tree 'App/Properties/AssemblyInfo.cs' "using System.Runtime.CompilerServices;`n[assembly: InternalsVisibleTo(""App.Tests"")]"
    # Thirty code-behind files, each with its .xaml, fill a folder to the limit.
    foreach ($index in 1..30) {
        $page = 'Page{0:D2}' -f $index
        Write-TreeFile $tree "App/Views/$page.xaml" '<Page />'
        Write-TreeFile $tree "App/Views/$page.xaml.cs" "namespace App.Views;`npublic sealed partial class $page { }"
    }
    Write-TreeFile $tree 'App/Services/Store.cs' @'
namespace App.Services
{
    using System;

    // namespace Fake; public class Fake
    internal sealed class Store
    {
        public string Name => "namespace Fake; public class Fake {";
    }
}
'@
    Write-TreeFile $tree 'App/Services/Data/Literals.cs' @'
namespace App.Services.Data;

/* namespace Fake;
   public class Fake { */
internal static class Literals
{
    /// <summary>namespace Fake; public class Fake</summary>
    public const string Verbatim = @"namespace Fake; ""public class Fake"" {";
    public const string Raw = """
        namespace Fake;
        public class Fake { "quoted" }
        """;
    public static string Interpolated(string[] items) => $"{string.Join("}", items)} namespace Fake; public class Fake {{";
    public static string VerbatimInterpolated(int x) => $@"{x} ""namespace Fake;"" {{ public class Fake";
    public const char Quote = '"';
    public const char Brace = '{';
#if NEVER
    // namespace Fake;
#endif
}
'@
    Write-TreeFile $tree 'App/Legacy/OldName.cs' "namespace App.OldName;`ninternal sealed class OldName { }"
    # A project inside another project's folder owns its own files.
    Write-TreeFile $tree 'App/Tools/Gen/Gen.csproj' '<Project Sdk="Microsoft.NET.Sdk" />'
    Write-TreeFile $tree 'App/Tools/Gen/Generator.cs' "namespace Gen;`ninternal sealed class Generator { }"

    Write-TreeFile $tree 'Lib/Lib.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RootNamespace>Lib</RootNamespace><AssemblyName>$(MSBuildProjectName)</AssemblyName></PropertyGroup></Project>'
    Write-TreeFile $tree 'Lib/PublicAPI.Shipped.txt' '#nullable enable'
    Write-TreeFile $tree 'Lib/LibOptions.cs' "namespace Lib;`npublic sealed record LibOptions(int Width);"
    Write-TreeFile $tree 'Lib/Layout/LayoutEngine.cs' @'
namespace Lib.Layout;

[System.Serializable]
public sealed class LayoutEngine<TItem, TKey>
    where TItem : class
    where TKey : struct
{
    internal sealed class Nested { }
}
'@
    Write-TreeFile $tree 'Lib/Layout/LayoutKind.cs' "namespace Lib.Layout;`npublic enum LayoutKind { Block, Inline }"
    Write-TreeFile $tree 'Lib/Layout/LayoutCallback.cs' "namespace Lib.Layout;`npublic delegate void LayoutCallback<T>(T item);"
    Write-TreeFile $tree 'Lib/Layout/Widget.cs' "namespace Lib.Layout;`npublic partial class Widget { }"
    Write-TreeFile $tree 'Lib/Layout/Widget.Interop.cs' "namespace Lib.Layout;`npartial class Widget { }"
    Write-TreeFile $tree 'Lib/Layout/Internal/LineBreaker.cs' "namespace Lib.Layout.Internal;`ninternal readonly record struct LineBreaker(int Width);"
    Write-TreeFile $tree 'Lib/Layout/Internal/Defaults.cs' "namespace Lib.Layout.Internal;`nstatic class Defaults { }"
    Write-TreeFile $tree 'Lib/Layout/Internal/Pool.cs' "namespace Lib.Layout.Internal`n{`n    internal sealed partial class Pool { }`n}"
    Write-TreeFile $tree 'Lib/Layout/Internal/Pool.Growth.cs' "namespace Lib.Layout.Internal;`npartial class Pool { }"

    Write-Exceptions $tree @($legacyException)
    # Build output, vendored code and generated files are skipped.
    Write-TreeFile $tree 'App/obj/Debug/Wrong.cs' "namespace Wrong;`npublic class Wrong { }"
    Write-TreeFile $tree 'App/bin/Wrong.cs' "namespace Wrong;`npublic class Wrong { }"
    Write-TreeFile $tree 'App/ThirdParty/Vendor/Vendor.cs' "namespace Vendor;`npublic class Vendor { }"
    Write-TreeFile $tree 'App/Generated/Bindings.g.cs' "namespace Wrong;`npublic class Bindings { }"
    Write-TreeFile $tree 'App/Generated/Bindings.g.i.cs' "namespace Wrong;`npublic class BindingsImpl { }"
    Write-TreeFile $tree 'App/Properties/Resources.Designer.cs' "namespace Wrong;`ninternal class Resources { }"
    return $tree
}

function Invoke-Check([string] $Tree) {
    $output = @(& $pwsh -NoLogo -NoProfile -File $script -Root $Tree 2>&1 | ForEach-Object { "$_" })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

$failures = [Collections.Generic.List[string]]::new()
# Each expected line is the start of one violation, in any order.
function Assert-Check([string] $Name, [scriptblock] $Break, [string[]] $Expected) {
    $tree = New-ConformingTree ($Name -replace '[^A-Za-z0-9]+', '-')
    if ($Break) { & $Break $tree }
    $result = Invoke-Check $tree
    $unmatched = [Collections.Generic.List[string]]::new([string[]]@($result.Output | Where-Object { $_ -match '^SRC\d{4} ' }))
    $missing = @(foreach ($line in $Expected) {
            $match = @($unmatched | Where-Object { $_.StartsWith($line, [StringComparison]::Ordinal) } | Select-Object -First 1)
            if ($match.Count -eq 0) { $line } else { [void] $unmatched.Remove($match[0]) }
        })
    $expectedExit = if ($Expected.Count -eq 0) { 0 } else { 1 }
    if ($result.ExitCode -eq $expectedExit -and $missing.Count -eq 0 -and $unmatched.Count -eq 0) {
        Write-Host "PASS $Name"
    }
    else {
        $failures.Add($Name)
        Write-Host "FAIL $Name (exit $($result.ExitCode)); missing: $($missing -join ' | ')"
        $result.Output | ForEach-Object { Write-Host "    $_" }
    }
}

try {
    Assert-Check 'conforming tree passes' $null @()

    Assert-Check 'SRC0001 root namespace differs from assembly name' {
        param($tree)
        Write-TreeFile $tree 'Extra/Extra.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AssemblyName>Extra.Core</AssemblyName></PropertyGroup></Project>'
        Write-TreeFile $tree 'Other/Other.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RootNamespace>Others</RootNamespace></PropertyGroup></Project>'
    } @(
        "SRC0001 Extra/Extra.csproj: RootNamespace 'Extra' differs from AssemblyName 'Extra.Core'."
        "SRC0001 Other/Other.csproj: RootNamespace 'Others' differs from AssemblyName 'Other'."
    )

    Assert-Check 'SRC0002 namespace is not the root namespace plus the folder' {
        param($tree)
        Write-TreeFile $tree 'App/Wrong.cs' "namespace App.Services;`ninternal sealed class Wrong { }"
        Write-TreeFile $tree 'App/Services/Misplaced.cs' "namespace App.Data;`ninternal sealed class Misplaced { }"
        Write-TreeFile $tree 'App/Services/Global.cs' 'internal sealed class Global { }'
        # Only a Program.cs with top-level statements may leave out its namespace.
        Write-TreeFile $tree 'App/Services/Program.cs' "internal static class Program`n{`n    private static void Main() { }`n}"
    } @(
        "SRC0002 App/Wrong.cs: namespace 'App.Services' does not match its folder. Use 'App',"
        "SRC0002 App/Services/Misplaced.cs: namespace 'App.Data' does not match its folder. Use 'App.Services',"
        "SRC0002 App/Services/Global.cs: declares no namespace. Declare namespace 'App.Services'."
        "SRC0002 App/Services/Program.cs: declares no namespace. Declare namespace 'App.Services'."
    )

    Assert-Check 'SRC0003 more than one namespace in a file' {
        param($tree)
        Write-TreeFile $tree 'App/Services/Two.cs' "namespace App.Services`n{`n}`nnamespace App.Other`n{`n}"
        Write-TreeFile $tree 'App/Services/Nested.cs' "namespace App.Services`n{`n    namespace Inner`n    {`n    }`n}"
    } @(
        'SRC0003 App/Services/Two.cs: declares 2 namespaces.'
        'SRC0003 App/Services/Nested.cs: declares 2 namespaces.'
    )

    Assert-Check 'SRC0004 folder over 30 files' {
        param($tree)
        Write-TreeFile $tree 'App/Views/Extra.cs' "namespace App.Views;`ninternal sealed class Extra { }"
    } @("SRC0004 App/Views: holds 31 C# files directly; the limit is 30.")

    Assert-Check 'SRC0005 project folder over 10 files' {
        param($tree)
        foreach ($index in 1..8) { Write-TreeFile $tree "App/Root$index.cs" "namespace App;`ninternal sealed class Root$index { }" }
    } @("SRC0005 App: is a project folder that holds 11 C# files directly; the limit is 10.")

    Assert-Check 'SRC0006 public type under Internal' {
        param($tree)
        Write-TreeFile $tree 'Lib/Layout/Internal/Leaked.cs' "namespace Lib.Layout.Internal;`npublic sealed class Leaked { }"
    } @("SRC0006 Lib/Layout/Internal/Leaked.cs: public class 'Leaked' is under an Internal folder.")

    Assert-Check 'SRC0007 non-public type outside Internal' {
        param($tree)
        Write-TreeFile $tree 'Lib/Layout/Hidden.cs' "namespace Lib.Layout;`ninternal sealed class Hidden { }"
        Write-TreeFile $tree 'Lib/Layout/Implicit.cs' "namespace Lib.Layout;`nsealed class Implicit { }"
        Write-TreeFile $tree 'Lib/Layout/Parts.cs' "namespace Lib.Layout;`npartial struct Parts { }"
        # A part with no modifier takes internal from the part that declares it.
        Write-TreeFile $tree 'Lib/Layout/Gadget.cs' "namespace Lib.Layout;`ninternal partial class Gadget { }"
        Write-TreeFile $tree 'Lib/Layout/Gadget.Part.cs' "namespace Lib.Layout;`npartial class Gadget { }"
    } @(
        "SRC0007 Lib/Layout/Hidden.cs: internal class 'Hidden' is outside an Internal folder."
        "SRC0007 Lib/Layout/Implicit.cs: internal class 'Implicit' is outside an Internal folder."
        "SRC0007 Lib/Layout/Parts.cs: internal struct 'Parts' is outside an Internal folder."
        "SRC0007 Lib/Layout/Gadget.cs: internal class 'Gadget' is outside an Internal folder."
        "SRC0007 Lib/Layout/Gadget.Part.cs: internal class 'Gadget' is outside an Internal folder."
    )

    Assert-Check 'an exception covers only the code and path it names' {
        param($tree)
        Write-TreeFile $tree 'App/Legacy/OldName.cs' "namespace App.OldName`n{`n}`nnamespace App.Legacy`n{`n}"
        Write-TreeFile $tree 'App/Legacy/Other.cs' "namespace App.OldName;`ninternal sealed class Other { }"
    } @(
        'SRC0003 App/Legacy/OldName.cs: declares 2 namespaces.'
        "SRC0002 App/Legacy/Other.cs: namespace 'App.OldName' does not match its folder."
    )

    Assert-Check 'SRC0008 exceptions that are incomplete or not current' {
        param($tree)
        Write-TreeFile $tree 'App/Views/Extra.cs' "namespace App.Views;`ninternal sealed class Extra { }"
        Write-Exceptions $tree @(
            $legacyException
            @{ code = 'SRC0002'; path = 'App/Legacy/OldName.cs'; reason = 'Listed twice.' }
            @{ path = 'App/Program.cs'; reason = 'No code.' }
            @{ code = 'SRC0004'; path = 'App/Views'; reason = '' }
            @{ code = 'SRC0002'; path = 'App/Services/Store.cs'; reason = 'Already fixed.' }
            @{ code = 'SRC0002'; path = 'Gone/Gone.cs'; reason = 'The file is gone.' }
        )
    } @(
        "SRC0008 ${exceptionsFile}: SRC0002 for 'App/Legacy/OldName.cs' is listed twice."
        "SRC0008 ${exceptionsFile}: an entry has no code or no path."
        "SRC0008 ${exceptionsFile}: SRC0004 for 'App/Views' has no reason."
        "SRC0008 ${exceptionsFile}: 'App/Services/Store.cs' no longer has a SRC0002 violation; remove its entry."
        "SRC0008 ${exceptionsFile}: 'Gone/Gone.cs' no longer has a SRC0002 violation; remove its entry."
    )

    Assert-Check 'SRC0002 without an exceptions file' {
        param($tree)
        [IO.File]::Delete((Join-Path $tree $exceptionsFile))
    } @("SRC0002 App/Legacy/OldName.cs: namespace 'App.OldName' does not match its folder. Use 'App.Legacy',")
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

if ($failures.Count -gt 0) {
    throw "$($failures.Count) source organization self-test case(s) failed: $($failures -join '; ')"
}
Write-Host 'Source organization check self-test passed.'
# The negative cases run the check in child processes that exit 1. End with an
# explicit success code so that last child's exit code is not the script's.
exit 0
