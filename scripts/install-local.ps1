<#
.SYNOPSIS
    Builds VsAgentic and installs it into your real Visual Studio instance for
    day-to-day use, replacing any previously installed copy.

.DESCRIPTION
    This is the dogfooding path: use your own build as your daily driver
    without publishing to the Marketplace.

    All Visual Studio instances must be closed — VSIXInstaller cannot replace
    an extension whose files are loaded, and the script refuses to run rather
    than leave you with a half-installed extension.

    Because the local build and the published extension share an Identity Id,
    Visual Studio treats them as the same extension. If the Marketplace
    version ever exceeds yours, VS's automatic extension update will silently
    replace your local build. Use -BumpPatch to stay ahead, and consider
    disabling automatic extension updates in
    Tools > Options > Environment > Extensions.

    Reinstalling the same version works because the script uninstalls first.
    VSIXInstaller rejects a package whose Identity Id and Version both match an
    already-installed extension, so an unbumped rebuild installed on its own
    would silently do nothing. The script verifies the uninstall and the install
    against the extension directory on disk rather than trusting exit codes.

.PARAMETER BumpPatch
    Increment the patch component of the version in source.extension.vsixmanifest
    before building. Keeps your local build ahead of the published version so VS
    never overwrites it and the update InfoBar stays quiet. Not required to pick
    up a rebuild - see above.

.PARAMETER SkipBuild
    Install the VSIX already present in bin\Release without rebuilding.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.EXAMPLE
    .\scripts\install-local.ps1
    Build and install the current version.

.EXAMPLE
    .\scripts\install-local.ps1 -BumpPatch
    Bump 3.4.4 to 3.4.5, build, and install.
#>
[CmdletBinding()]
param(
    [switch]$BumpPatch,
    [switch]$SkipBuild,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# Identity Id from source.extension.vsixmanifest. VSIXInstaller uninstalls by
# this, not by display name.
$ExtensionId = 'VsAgentic.VSExtension.c3d4e5f6-a7b8-4c9d-0e1f-2a3b4c5d6e7f'

$repoRoot    = Split-Path -Parent $PSScriptRoot
$solution    = Join-Path $repoRoot 'src\VsAgentic.slnx'
$manifest    = Join-Path $repoRoot 'src\VsAgentic.VSExtension\source.extension.vsixmanifest'
$vsixPath    = Join-Path $repoRoot "src\VsAgentic.VSExtension\bin\$Configuration\net472\VsAgentic.VSExtension.vsix"

function Write-Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

# Reads the version baked into a built .vsix. This is the only version that
# matters: the source manifest can be ahead of the artifact (a -BumpPatch run
# that bumped and then failed to build, or -SkipBuild against a stale bin).
function Get-VsixVersion($path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $zip.Entries | Where-Object { $_.Name -eq 'extension.vsixmanifest' } | Select-Object -First 1
        if (-not $entry) { throw "'$path' contains no extension.vsixmanifest." }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { [xml]$x = $reader.ReadToEnd() } finally { $reader.Dispose() }
        return $x.PackageManifest.Metadata.Identity.Version
    } finally {
        $zip.Dispose()
    }
}

# Every per-user extension directory currently holding our Identity Id.
# More than one means a previous uninstall left a copy behind.
function Get-InstalledCopies {
    $root = Join-Path $env:LocalAppData 'Microsoft\VisualStudio'
    if (-not (Test-Path $root)) { return @() }
    return @(
        Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'Extensions' } |
            Where-Object { Test-Path $_ } |
            Get-ChildItem -Directory -ErrorAction SilentlyContinue |
            ForEach-Object {
                $m = Join-Path $_.FullName 'extension.vsixmanifest'
                if (Test-Path $m) {
                    try {
                        [xml]$x = Get-Content $m -ErrorAction Stop
                        if ($x.PackageManifest.Metadata.Identity.Id -eq $ExtensionId) {
                            [pscustomobject]@{
                                Path    = $_.FullName
                                Version = $x.PackageManifest.Metadata.Identity.Version
                            }
                        }
                    } catch { }
                }
            }
    )
}

# --- 1. Refuse to run while Visual Studio is open ------------------------------
# VSIXInstaller will either fail or half-apply against a running IDE, and the
# Experimental Instance counts too.
$running = @(Get-Process -Name devenv -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "Visual Studio is running ($($running.Count) instance(s), PID: $($running.Id -join ', '))." -ForegroundColor Red
    Write-Host "Close every VS window - including any Experimental Instance - and run this again." -ForegroundColor Red
    exit 1
}

# --- 2. Locate VSIXInstaller.exe ----------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found at '$vswhere'. Is Visual Studio installed?"
}

$vsInstallPath = & $vswhere -latest -prerelease -property installationPath
if ([string]::IsNullOrWhiteSpace($vsInstallPath)) {
    throw "vswhere could not locate a Visual Studio installation."
}

$vsixInstaller = Join-Path $vsInstallPath 'Common7\IDE\VSIXInstaller.exe'
if (-not (Test-Path $vsixInstaller)) {
    throw "VSIXInstaller.exe not found at '$vsixInstaller'."
}
Write-Host "Visual Studio: $vsInstallPath" -ForegroundColor DarkGray

# --- 3. Optionally bump the patch version -------------------------------------
if ($BumpPatch) {
    Write-Step "Bumping version"

    # Edited as text, not via [xml].Save(): the XmlDocument round-trip reflows
    # every wrapped attribute onto one line and drops the trailing newline,
    # turning a one-digit bump into a whole-file diff.
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    $text    = [System.IO.File]::ReadAllText($manifest)
    $pattern = '(?<head><Identity\b[^>]*?\bVersion=")(?<ver>[^"]+)(?<tail>")'

    $match = [regex]::Match($text, $pattern)
    if (-not $match.Success) {
        throw "Could not find the Identity Version attribute in '$manifest'; bump it by hand."
    }

    $current = $match.Groups['ver'].Value
    $parts   = $current.Split('.')
    if ($parts.Count -lt 3) {
        throw "Version '$current' in the manifest is not in major.minor.patch form; bump it by hand."
    }
    $parts[2] = ([int]$parts[2] + 1).ToString()
    $new = $parts -join '.'

    $text = $text.Remove($match.Index, $match.Length).Insert($match.Index, "$($match.Groups['head'].Value)$new$($match.Groups['tail'].Value)")
    [System.IO.File]::WriteAllText($manifest, $text, $utf8Bom)

    Write-Host "  $current -> $new" -ForegroundColor Green
    Write-Host "  (the publish workflow rewrites this from the git tag, so a local bump is safe)" -ForegroundColor DarkGray
}

[xml]$manifestXml = Get-Content $manifest
$manifestVersion = $manifestXml.PackageManifest.Metadata.Identity.Version
Write-Host "Manifest version: $manifestVersion" -ForegroundColor DarkGray

$before = @(Get-InstalledCopies)
if ($before.Count -eq 0) {
    Write-Host "Currently installed: none" -ForegroundColor DarkGray
} else {
    Write-Host "Currently installed: $(($before | ForEach-Object { $_.Version }) -join ', ')" -ForegroundColor DarkGray
}

# --- 4. Build -----------------------------------------------------------------
if ($SkipBuild) {
    Write-Step "Skipping build (-SkipBuild)"
} else {
    Write-Step "Building $Configuration"
    & dotnet build $solution -c $Configuration -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path $vsixPath)) {
    throw "VSIX not found at '$vsixPath'. Build the solution first, or drop -SkipBuild."
}
$sizeMb = [math]::Round((Get-Item $vsixPath).Length / 1MB, 2)
Write-Host "VSIX: $vsixPath ($sizeMb MB)" -ForegroundColor DarkGray

# The artifact, not the manifest, is what gets installed. They drift whenever a
# -BumpPatch run bumps the version and then doesn't complete a build, and
# -SkipBuild can't reconcile them at all.
$version = Get-VsixVersion $vsixPath
Write-Host "Version to install: $version" -ForegroundColor DarkGray
if ($version -ne $manifestVersion) {
    Write-Host ""
    Write-Host "The built VSIX is $version but source.extension.vsixmanifest says $manifestVersion." -ForegroundColor Red
    if ($SkipBuild) {
        throw "Stale VSIX in bin\$Configuration. Drop -SkipBuild so the $manifestVersion artifact gets built."
    }
    # The VSSDK packs obj\<config>\<tfm>\extension.vsixmanifest, and
    # DetokenizeVsixManifestSource only writes that file when it is missing.
    # VsAgentic.VSExtension.csproj has a target to clear it, so reaching this
    # point means that target is not doing its job.
    Write-Host "Delete this and rebuild:" -ForegroundColor Red
    Write-Host "  src\VsAgentic.VSExtension\obj\$Configuration\net472\extension.vsixmanifest" -ForegroundColor Red
    throw "The build did not pick up the manifest version."
}

# --- 5. Uninstall the previous copy -------------------------------------------
# Leaving the old one in place can produce two extension directories, which is
# the cause of the "update banner keeps appearing" symptom in the README.
# A missing extension is not an error here.
#
# This step is also what makes reinstalling the *same* version work at all:
# VSIXInstaller refuses a package whose Id and Version already match an
# installed extension, so without a preceding uninstall an unbumped rebuild is
# a no-op. Hence the disk check below rather than trusting the exit code - a
# silently failed uninstall turns the install into that no-op.
Write-Step "Uninstalling any previously installed VsAgentic"
if ($before.Count -eq 0) {
    Write-Host "  Nothing installed - skipping." -ForegroundColor DarkGray
} else {
    $uninstall = Start-Process -FilePath $vsixInstaller `
                               -ArgumentList @('/q', "/u:$ExtensionId") `
                               -Wait -PassThru -NoNewWindow

    $left = @(Get-InstalledCopies)
    if ($left.Count -gt 0) {
        Write-Host "Uninstall left $($left.Count) copy/copies on disk (installer exit code $($uninstall.ExitCode)):" -ForegroundColor Red
        $left | ForEach-Object { Write-Host "  $($_.Version)  $($_.Path)" -ForegroundColor Red }
        Write-Host "Installing over these would be a no-op. See the newest %TEMP%\dd_VSIXInstaller_*.log." -ForegroundColor Red
        throw "Could not remove the previously installed VsAgentic."
    }
    Write-Host "  Removed (exit code $($uninstall.ExitCode))." -ForegroundColor Green
}

# --- 6. Install ---------------------------------------------------------------
# Not /q: the unsigned-extension prompt needs to be visible and accepted.
Write-Step "Installing $version"
Write-Host "Check for a popup on the desktop from VS Installer window that require your input"
$install = Start-Process -FilePath $vsixInstaller `
                         -ArgumentList @($vsixPath) `
                         -Wait -PassThru -NoNewWindow
if ($install.ExitCode -ne 0) {
    throw "VSIXInstaller failed with exit code $($install.ExitCode). See the newest %TEMP%\dd_VSIXInstaller_*.log."
}

# Exit code 0 is not proof: verify the bits actually landed.
$after = @(Get-InstalledCopies)
if ($after.Count -eq 0) {
    throw "VSIXInstaller reported success but no VsAgentic extension directory exists. See the newest %TEMP%\dd_VSIXInstaller_*.log."
}
$wrong = @($after | Where-Object { $_.Version -ne $version })
if ($wrong.Count -gt 0) {
    $wrong | ForEach-Object { Write-Host "  found $($_.Version) at $($_.Path)" -ForegroundColor Red }
    throw "Expected $version to be installed but found the above instead."
}
Write-Host "  Verified at $($after[0].Path)" -ForegroundColor DarkGray

Write-Step "Done"
Write-Host "VsAgentic $version installed. Start Visual Studio and open View > Other Windows > VsAgentic." -ForegroundColor Green
Write-Host ""
Write-Host "Settings and chat history are preserved across reinstalls:" -ForegroundColor DarkGray
Write-Host "  Tools > Options > VsAgentic  (per-instance settings hive)" -ForegroundColor DarkGray
Write-Host "  %AppData%\VsAgentic\workspaces  (session history)" -ForegroundColor DarkGray
