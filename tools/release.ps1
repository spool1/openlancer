<#
.SYNOPSIS
Builds the binary release zip of OpenLancer from one commit in a clean clone.

.DESCRIPTION
The steps follow section 7.1 of the R1 release plan:
 1. Preflight: the tools, the work root, the commits on origin, the version of the commit and the name of the zip.
 2. A clean clone of the commit and its submodules, and the content scan of the clone.
 3. The engine natives and the shader tools of the clone (natives with the static C runtime, OL-0035).
 4. The OpenLancer tests and the engine fork tests in Release, then the timing gates in a separate run (see the
    notes).
 5. A self-contained win-x64 publish of each program, with the engine commit for Help > About.
 6. One stage folder from the three publish folders (tools/release_check.py merge).
 7. README-FIRST.txt, LICENSE.txt, NOTICE.txt and the licenses folder (tools/release_check.py docs).
 8. The checks a to i of the stage folder (tools/release_check.py stage).
 9. A start of oltool, and a load of each native DLL.
10. The deterministic zip and the checks of its entries (tools/release_check.py zip).
11. The outputs in <work root>\out: the zip, its .sha256 file, the OpenAL Soft source archive, the release notes with
    the commits filled in (when the commit has a notes file), and the log.
The script writes only below the work root. It never tags, pushes or uploads.

.PARAMETER Version
The release version, for example 1.0.0-beta.1. It must be the version in src/Directory.Build.props of the commit. A
release refuses a development version, for example 1.0.0-dev.

.PARAMETER Commit
The commit to build. The default is the commit of origin/master.

.PARAMETER WorkRoot
The folder for the clone and the outputs. The default is the folder olrel at the root of the drive that holds the
repo. So the default is not on the system drive when the repo is not on it. The work root must not be in the repo. Its
path must not hold the user name or the word "decompiled". The native binaries keep the source paths of the build.

.PARAMETER DryRun
A test run. It allows another version than the version of the commit, a development version, texts with marks, a
component with a status and no release notes. The zip gets the name OpenLancer-<version>-dryrun-<commit>-win-x64.zip,
with the version of the commit.

.PARAMETER SkipTests
Only with -DryRun: skip step 4.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 1.0.0-dev -DryRun

.NOTES
Versions and releases:
- Master holds the next version with the suffix dev, for example 1.0.0-dev. A release commit sets the version of the
  release in src/Directory.Build.props, for example 1.0.0-beta.1, 1.0.0-rc.1 or 1.0.0.
- After each release, master goes to the next version with -dev.
- A beta or a release candidate is a GitHub pre-release. Only the final version is Latest.
- Each version needs its notes file release/notes/v<version>.md. One approval of the owner covers the notes files of
  the beta, the release candidates and the final version of one release.
- Each notes file needs its entry in MARKDOWN_FILES of tools/content_scan.py.

Tests:
- Step 4 leaves out PlayCopyRetailTests, HdePlayCopyRetailTests, RandomEditTests and SampleModTests. They write a play
  copy or test output into the output folder next to the game data folder. The script writes only below the work root.
- A timing gate (a test with the trait Category=Performance) measures time. Load on the computer can make a gate fail.
  So step 4 runs the timing gates alone, after the other tests. When a gate fails, the step runs the timing gates one
  more time. A second failure stops the release.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Commit,
    [string]$WorkRoot,
    [switch]$DryRun,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$DevRepo = Split-Path -Parent $PSScriptRoot
# The default work root. The help of -WorkRoot and the preflight messages name this rule.
$WorkRootRule = 'the folder olrel at the root of the drive that holds the repo'
if (-not $WorkRoot) { $WorkRoot = Join-Path ([System.IO.Path]::GetPathRoot($DevRepo)) 'olrel' }
$script:LogFile = $null

function Write-Log([string]$Text) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Text
    Write-Host $line
    if ($script:LogFile) { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 }
}

function Stop-Release([string]$Text) {
    Write-Log "STOP: $Text"
    throw $Text
}

# Runs a program and stops the release when it gives an exit code other than the expected code. With -Tries 2, the
# first other exit code runs the program one more time.
function Invoke-Native {
    param([string]$File, [string[]]$Arguments, [string]$Folder = (Get-Location).Path, [int]$Expected = 0,
        [int]$Tries = 1)
    for ($try = 1; ; $try++) {
        Write-Log ("> {0} {1}" -f $File, ($Arguments -join ' '))
        Push-Location -LiteralPath $Folder
        try {
            & $File @Arguments
            $code = $LASTEXITCODE
        } finally {
            Pop-Location
        }
        if ($code -eq $Expected) { return }
        if ($try -ge $Tries) { Stop-Release "$File exited with code $code." }
        Write-Log "$File exited with code $code. Try $($try + 1) of $Tries."
    }
}

# Runs a program and returns its output as one trimmed string. When the program fails, the stop also names its output.
function Get-NativeOutput([string]$File, [string[]]$Arguments) {
    $lines = & $File @Arguments
    $text = ($lines | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { Stop-Release "$File $($Arguments -join ' ') exited with code $LASTEXITCODE. $text" }
    return $text
}

function Find-Tool([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) { Stop-Release "Install $Name, or add it to PATH." }
    return $command.Source
}

function Remove-Below([string]$Path) {
    # Deletes an output folder of an earlier run. Only folders below the work root.
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = [System.IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($script:Root + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Release "Refused to delete $full. It is not below the work root."
    }
    Remove-Item -LiteralPath $full -Recurse -Force
}

# ---------------------------------------------------------------------------------------------------------------------
# 1. Preflight

if ($SkipTests -and -not $DryRun) { throw 'A release cannot skip the tests. Use -SkipTests only with -DryRun.' }
if (-not [Environment]::Is64BitProcess) { throw 'Start the script in 64-bit PowerShell. Step 9 loads the x64 natives.' }

$script:Root = [System.IO.Path]::GetFullPath($WorkRoot).TrimEnd('\')
$devFull = [System.IO.Path]::GetFullPath($DevRepo).TrimEnd('\')
if ($script:Root -eq $devFull -or $script:Root.StartsWith($devFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "The work root $script:Root is in the repo. Use a folder outside the repo, for example the default: $WorkRootRule."
}
if ($env:USERNAME -and $script:Root.IndexOf($env:USERNAME, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "The work root $script:Root holds the user name. The natives keep their source paths. Use the default: $WorkRootRule."
}
if ($script:Root.IndexOf('decompiled', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "The work root $script:Root holds the word 'decompiled'. Use the default: $WorkRootRule."
}

$src = Join-Path $script:Root 'src'
$publish = Join-Path $script:Root 'publish'
$stageRoot = Join-Path $script:Root 'stage'
$out = Join-Path $script:Root 'out'
$cache = Join-Path $script:Root 'cache'
New-Item -ItemType Directory -Force -Path $script:Root, $out, $cache | Out-Null
$script:LogFile = Join-Path $out ("release-{0}.log" -f $Version)
Set-Content -LiteralPath $script:LogFile -Value "OpenLancer release $Version" -Encoding UTF8

$git = Find-Tool 'git'
$dotnet = Find-Tool 'dotnet'
$python = Find-Tool 'python'
$cmakeFolder = Join-Path $env:ProgramFiles 'CMake\bin'
if (Test-Path -LiteralPath (Join-Path $cmakeFolder 'cmake.exe')) { $env:PATH = "$cmakeFolder;$env:PATH" }
$cmake = Find-Tool 'cmake'
$cmakeVersion = [version](((& $cmake --version) | Select-Object -First 1) -replace '^cmake version\s+([0-9.]+).*$', '$1')
if ($cmakeVersion -lt [version]'4.2') { Stop-Release "CMake $cmakeVersion is too old. The engine needs CMake 4.2 or later." }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { Stop-Release 'Install Visual Studio with the Desktop C++ workload.' }
$vs = Get-NativeOutput $vswhere @('-latest', '-products', '*', '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-property', 'installationPath')
if (-not $vs) { Stop-Release 'Visual Studio has no C++ tools. Install the Desktop C++ workload.' }
Write-Log "git: $(Get-NativeOutput $git @('--version'))"
Write-Log "dotnet: $(Get-NativeOutput $dotnet @('--version'))"
Write-Log "python: $(Get-NativeOutput $python @('--version'))"
Write-Log "cmake: $cmakeVersion"
Write-Log "Visual Studio: $vs"

Invoke-Native $git @('-C', $DevRepo, 'fetch', 'origin', 'master')
if (-not $Commit) { $Commit = Get-NativeOutput $git @('-C', $DevRepo, 'rev-parse', 'origin/master') }
$sha = Get-NativeOutput $git @('-C', $DevRepo, 'rev-parse', "$Commit^{commit}")
& $git -C $DevRepo merge-base --is-ancestor $sha origin/master
if ($LASTEXITCODE -ne 0) { Stop-Release "The commit $sha is not on origin/master." }
$forkSha = ((Get-NativeOutput $git @('-C', $DevRepo, 'ls-tree', $sha, 'external/Librelancer')) -split '\s+')[2]
$forkRepo = Join-Path $DevRepo 'external\Librelancer'
Invoke-Native $git @('-C', $forkRepo, 'fetch', 'origin', 'openlancer')
& $git -C $forkRepo merge-base --is-ancestor $forkSha origin/openlancer
if ($LASTEXITCODE -ne 0) { Stop-Release "The engine commit $forkSha is not on origin/openlancer of the fork." }
$sha7 = $sha.Substring(0, 7)
$commitTime = Get-NativeOutput $git @('-C', $DevRepo, 'show', '-s', '--format=%ct', $sha)

$props = [xml](Get-NativeOutput $git @('-C', $DevRepo, 'show', "${sha}:src/Directory.Build.props"))
$prefixNode = $props.SelectSingleNode('//VersionPrefix')
$suffixNode = $props.SelectSingleNode('//VersionSuffix')
if ($null -eq $prefixNode) { Stop-Release "src/Directory.Build.props of $sha has no VersionPrefix." }
$prefix = $prefixNode.InnerText.Trim()
$buildVersion = if ($null -ne $suffixNode -and $suffixNode.InnerText.Trim()) { "$prefix-$($suffixNode.InnerText.Trim())" } else { $prefix }
if ($buildVersion -ne $Version -and -not $DryRun) {
    Stop-Release "The commit $sha has the version $buildVersion, not $Version. Change src/Directory.Build.props first."
}
# The name rule of tools/release_check.py. It also refuses a release of a development version, before the clone.
$nameArgs = @('-B', (Join-Path $PSScriptRoot 'release_check.py'), 'name', '--version', $buildVersion, '--sha7', $sha7)
if ($DryRun) { $nameArgs += '--dry-run' }
$zipName = Get-NativeOutput $python $nameArgs
Write-Log "Commit $sha, engine commit $forkSha, version $buildVersion, dry run: $DryRun"

# ---------------------------------------------------------------------------------------------------------------------
# 2. Clone

Remove-Below $src
Remove-Below $publish
Remove-Below $stageRoot
$origin = Get-NativeOutput $git @('-C', $DevRepo, 'remote', 'get-url', 'origin')
Invoke-Native $git @('clone', '--no-checkout', $origin, $src)
Invoke-Native $git @('-C', $src, 'checkout', '--detach', $sha)
Invoke-Native $git @('-C', $src, 'submodule', 'update', '--init', '--recursive')
Invoke-Native $python @('-B', 'tools\content_scan.py') $src
Invoke-Native $python @('-B', 'tools\content_scan.py', '--fork') $src

# ---------------------------------------------------------------------------------------------------------------------
# 3. Engine natives and shader tools

$engine = Join-Path $src 'external\Librelancer'
Invoke-Native 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $engine 'build.ps1'),
    '--assemblyversion', $buildVersion, 'GenerateVersion', 'BuildNatives', 'ShaderDependencies') $engine

# ---------------------------------------------------------------------------------------------------------------------
# 4. Tests

if ($SkipTests) {
    Write-Log 'Tests skipped (-SkipTests, dry run only).'
} else {
    $base = $env:OPENLANCER_BASE
    if (-not $base) { $base = Join-Path (Split-Path -Parent $DevRepo) 'GameData\base' }
    if (Test-Path -LiteralPath (Join-Path $base 'EXE')) {
        $env:OPENLANCER_BASE = $base
        Write-Log 'Retail tests use the base data folder (read only).'
    } else {
        Write-Log 'No base data folder. The retail tests skip.'
    }
    # The filter of PlayCopyRetailTests also matches HdePlayCopyRetailTests.
    $notNextToData = 'FullyQualifiedName!~PlayCopyRetailTests&FullyQualifiedName!~RandomEditTests&FullyQualifiedName!~SampleModTests'
    Write-Log ('The tests leave out PlayCopyRetailTests, HdePlayCopyRetailTests, RandomEditTests and SampleModTests. ' +
        'They write a play copy or test output into the output folder next to the game data folder.')
    Write-Log 'The first test run leaves out the timing gates (Category=Performance). A separate run tests them.'
    Invoke-Native $dotnet @('test', 'OpenLancer.slnx', '-c', 'Release', '--nologo', '--filter', "$notNextToData&Category!=Performance") $src
    Invoke-Native $dotnet @('test', 'external\Librelancer\src\LibreLancer.Tests\LibreLancer.Tests.csproj', '-c', 'Release', '--nologo') $src
    Write-Log 'The timing gates run alone. When a gate fails, the step runs the timing gates one more time.'
    Invoke-Native $dotnet @('test', 'tests\OpenLancer.DataTests', '-c', 'Release', '--no-build', '--nologo', '--filter',
        "Category=Performance&$notNextToData") $src -Tries 2
}

# ---------------------------------------------------------------------------------------------------------------------
# 5. Publish

$programs = [ordered]@{ 'OpenLancer.Play' = 'OpenLancer.Play'; 'oltool' = 'OpenLancer.Tools'; 'OpenLancer.Editor' = 'OpenLancer.Editor' }
$merges = @()
foreach ($program in $programs.Keys) {
    $project = $programs[$program]
    $folder = Join-Path $publish $project
    Invoke-Native $dotnet @('publish', "src\$project\$project.csproj", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-o', $folder, "-p:PathMap=$src=/_/", '-p:EnableSourceLink=false', '-p:DebugType=embedded',
        "-p:SourceRevisionId=$sha7", "-p:OpenLancerEngineCommit=$forkSha", '--nologo') $src
    $merges += "$program=$folder"
}

# ---------------------------------------------------------------------------------------------------------------------
# 6. to 8. Stage folder, documents and checks

$stage = Join-Path $stageRoot "OpenLancer-v$buildVersion"
$check = Join-Path $src 'tools\release_check.py'
Invoke-Native $python (@('-B', $check, 'merge', $stage) + $merges)
$notesOut = Join-Path $out "release-notes-v$buildVersion.md"
if (Test-Path -LiteralPath $notesOut) { Remove-Item -LiteralPath $notesOut -Force }
$docsArgs = @('-B', $check, 'docs', $stage, '--repo', $src, '--version', $buildVersion, '--commit', $sha,
    '--fork-commit', $forkSha, '--notes-out', $notesOut)
if ($DryRun) { $docsArgs += '--dry-run' }
Invoke-Native $python $docsArgs
if (-not (Test-Path -LiteralPath $notesOut)) {
    Write-Log "The commit has no release/notes/v$buildVersion.md. The dry run writes no release notes."
}
$forbid = @('--forbid', $devFull, '--forbid', (Split-Path -Parent $devFull))
Invoke-Native $python (@('-B', $check, 'stage', $stage, '--repo', $src, '--version', $buildVersion) + $forbid)

# ---------------------------------------------------------------------------------------------------------------------
# 9. Start check

# oltool with no arguments prints its usage and exits with 2. That proves that the self-contained runtime starts.
Invoke-Native (Join-Path $stage 'oltool.exe') @() $stage 2
Add-Type -Namespace OpenLancerRelease -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr LoadLibraryExW(string path, System.IntPtr file, uint flags);
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern bool FreeLibrary(System.IntPtr module);
'@
# LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32: each native finds only its own folder and Windows.
$flags = [uint32](0x100 -bor 0x800)
foreach ($dll in Get-ChildItem -LiteralPath (Join-Path $stage 'x64') -Filter '*.dll') {
    $module = [OpenLancerRelease.Native]::LoadLibraryExW($dll.FullName, [IntPtr]::Zero, $flags)
    if ($module -eq [IntPtr]::Zero) {
        Stop-Release ("x64\{0} does not load (error {1})." -f $dll.Name, [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    [void][OpenLancerRelease.Native]::FreeLibrary($module)
    Write-Log "x64\$($dll.Name) loads."
}

# ---------------------------------------------------------------------------------------------------------------------
# 10. and 11. Zip and outputs

$zip = Join-Path $out $zipName
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Invoke-Native $python (@('-B', $check, 'zip', $stage, $zip, '--repo', $src, '--time', $commitTime) + $forbid)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $zipName" -Encoding ASCII

$thirdParty = Get-Content -LiteralPath (Join-Path $src 'release\third-party.json') -Raw | ConvertFrom-Json
$openal = $thirdParty.openal_source
if ($openal.sha256) {
    $archive = Join-Path $cache $openal.file
    if (-not (Test-Path -LiteralPath $archive)) {
        Write-Log "Download $($openal.url)"
        Invoke-WebRequest -Uri $openal.url -OutFile $archive -UseBasicParsing
    }
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($archiveHash -ne $openal.sha256.ToLowerInvariant()) {
        Stop-Release "$($openal.file) has the SHA-256 $archiveHash, not the hash in release/third-party.json."
    }
    Copy-Item -LiteralPath $archive -Destination (Join-Path $out $openal.file) -Force
} else {
    Write-Log 'release/third-party.json has no SHA-256 for the OpenAL Soft archive. The dry run does not copy it.'
}

$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File)
$size = ($files | Measure-Object -Property Length -Sum).Sum
Write-Log ("Stage: {0} files, {1:N1} MB. Zip: {2:N1} MB, SHA-256 {3}" -f $files.Count, ($size / 1MB), ((Get-Item -LiteralPath $zip).Length / 1MB), $hash)
Write-Log "Outputs in $out"
Write-Log 'All checks passed.'
