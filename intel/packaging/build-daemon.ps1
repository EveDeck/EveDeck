<#
.SYNOPSIS
    Builds the EveDeck Intel daemon into a self-contained Windows app image and zips it.

.DESCRIPTION
    Produces a folder the user can unzip anywhere and double-click: the launcher, a trimmed Java
    runtime, and the config template. Nothing has to be installed first -- a daemon that opens a
    "please install Java" dialog would be dead on arrival for the audience this is aimed at.

    Two JDKs are in play on purpose. Gradle builds with whatever JAVA_HOME points at (in this
    workspace, Android Studio's JBR, because the Android module needs it), but JetBrains strips
    jpackage out of the JBR, so packaging uses a full JDK found separately.

.PARAMETER Version
    Version stamped into the launcher and the zip name. Keep it in step with the intel-v* tag.

.PARAMETER JdkHome
    A JDK containing bin\jpackage.exe. Discovered automatically if not given.

.EXAMPLE
    pwsh packaging\build-daemon.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [string]$JdkHome
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "build\dist"
$appName = "EveDeck Intel"

function Find-Jpackage {
    if ($JdkHome) {
        $candidate = Join-Path $JdkHome "bin\jpackage.exe"
        if (Test-Path $candidate) { return $candidate }
        throw "No jpackage.exe under -JdkHome '$JdkHome'."
    }
    $found = Get-ChildItem "$env:ProgramFiles\*\*\bin\jpackage.exe", "$env:ProgramFiles\*\bin\jpackage.exe" `
        -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.FullName }
    throw "jpackage.exe not found. Install a full JDK (the Android Studio JBR does not ship one) or pass -JdkHome."
}

$jpackage = Find-Jpackage
Write-Host "jpackage:  $jpackage"

# --- 1. Build the runnable image Gradle already knows how to assemble ------------------------
Write-Host "Building :daemon:installDist"
Push-Location $root
try {
    & "$root\gradlew.bat" ":daemon:installDist" --quiet
    if ($LASTEXITCODE -ne 0) { throw "Gradle build failed." }
} finally {
    Pop-Location
}

$lib = Join-Path $root "daemon\build\install\daemon\lib"
if (-not (Test-Path (Join-Path $lib "daemon.jar"))) { throw "daemon.jar missing from $lib." }

# --- 2. Package ------------------------------------------------------------------------------
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist -Force | Out-Null

# Named explicitly rather than shipping the whole JDK: it roughly halves the download, and an
# omission shows up immediately as a NoClassDefFoundError rather than as a subtle runtime gap.
#   java.desktop          tray, settings window, file chooser
#   java.net.http/jgss    ESI lookups
#   jdk.crypto.ec         the TLS curves ESI actually negotiates
#   jdk.unsupported       sun.misc.Unsafe, which the coroutines runtime still reaches for
$modules = @(
    "java.base", "java.desktop", "java.logging", "java.management", "java.naming",
    "java.net.http", "java.security.jgss", "java.sql", "java.xml",
    "jdk.crypto.ec", "jdk.crypto.cryptoki", "jdk.unsupported", "jdk.zipfs"
) -join ","

& $jpackage `
    --type app-image `
    --name $appName `
    --app-version $Version `
    --vendor "EveDeck" `
    --description "Mirrors EVE Online intel channels to an Android tablet over your LAN." `
    --icon (Join-Path $PSScriptRoot "eveintel.ico") `
    --input $lib `
    --main-jar "daemon.jar" `
    --main-class "dev.eveintel.daemon.MainKt" `
    --add-modules $modules `
    --java-options "-Xmx512m" `
    --java-options "-Dfile.encoding=UTF-8" `
    --dest $dist
if ($LASTEXITCODE -ne 0) { throw "jpackage failed." }

$appDir = Join-Path $dist $appName

# The template rides along beside the exe: DesktopSession seeds the live config from it on first
# run, so the file the user opens has its comments rather than only the written keys.
Copy-Item (Join-Path $root "eveintel.properties.example") (Join-Path $appDir "eveintel.properties.example")
Copy-Item (Join-Path $PSScriptRoot "READ ME FIRST.txt") (Join-Path $appDir "READ ME FIRST.txt")

# --- 3. Zip ----------------------------------------------------------------------------------
$zip = Join-Path $dist "EveDeckIntel-daemon-$Version-win-x64.zip"
Compress-Archive -Path $appDir -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "app image: $appDir"
Write-Host "zip:       $zip  ($size MB)"
Write-Host "sha256:    $((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())"
