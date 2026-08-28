<#
.SYNOPSIS
  Submits an MSIX to the Microsoft Store via the Store submission REST API.

.DESCRIPTION
  Replaces the `msstore publish` CLI, which cannot be used here: that command detects a PROJECT
  type (.NET MAUI, Flutter, PWA, Electron, ...) and drives packaging itself. EveDeck is a plain WPF
  csproj, which it does not implement, so it fails with "could not find a project publisher" no
  matter what arguments it is given. Its lower-level `submission` subcommands can read and update a
  draft's JSON but have no way to upload a package. The REST API has none of those limits.

  Flow (Microsoft Store submission API v1.0):
    1. Acquire a token via client credentials.
    2. Delete any pending submission (only one may exist at a time).
    3. Create a new submission. It is CLONED from the last published one, so listing text,
       screenshots, pricing and age ratings all carry over untouched; only packages change.
    4. Mark the existing packages PendingDelete and add ours as PendingUpload.
    5. Upload a zip of the package to the SAS URL the submission handed us.
    6. Commit, then poll until the Store stops reporting CommitStarted.

.PARAMETER MsixPath
  The .msix to submit.

.PARAMETER ApplicationId
  Store application id (e.g. 9N77Q5HC4R56).

.PARAMETER TenantId / ClientId / ClientSecret
  Entra credentials for an app registered in Partner Center with the MANAGER role. Default to the
  PARTNER_CENTER_* environment variables so CI can pass them as secrets rather than arguments.

.PARAMETER Commit
  Actually commit the submission, which starts certification. WITHOUT this switch the script stops
  after the upload and leaves the submission in draft for review in Partner Center. Defaulting to
  draft is deliberate: committing is the irreversible half, and certification is measured in days.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$MsixPath,
    [string]$ApplicationId = $env:STORE_PRODUCT_ID,
    [string]$TenantId      = $env:PARTNER_CENTER_TENANT_ID,
    [string]$ClientId      = $env:PARTNER_CENTER_CLIENT_ID,
    [string]$ClientSecret  = $env:PARTNER_CENTER_CLIENT_SECRET,
    [switch]$Commit
)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"   # a visible progress bar makes the 80MB PUT crawl

foreach ($pair in @{ MsixPath = $MsixPath; ApplicationId = $ApplicationId; TenantId = $TenantId
                     ClientId = $ClientId; ClientSecret = $ClientSecret }.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($pair.Value)) { throw "$($pair.Key) is required." }
}
if (-not (Test-Path $MsixPath)) { throw "MSIX not found: $MsixPath" }

$msix    = Get-Item $MsixPath
$apiRoot = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$ApplicationId"

Write-Host "== submitting $($msix.Name) ($([math]::Round($msix.Length / 1MB, 1)) MB) to $ApplicationId =="

# -- 1. token ------------------------------------------------------------------------------------
$token = (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -Body @{
    grant_type    = "client_credentials"
    client_id     = $ClientId
    client_secret = $ClientSecret
    scope         = "https://manage.devcenter.microsoft.com/.default"
}).access_token
if (-not $token) { throw "Failed to acquire an access token." }
$headers = @{ Authorization = "Bearer $token" }
Write-Host "authenticated"

# -- 2. clear any pending submission --------------------------------------------------------------
# The API allows exactly one in-flight submission. A leftover draft (an earlier failed run, or one
# started by hand in Partner Center) makes the create in step 3 fail, so clear it first.
$app = Invoke-RestMethod -Method Get -Uri $apiRoot -Headers $headers
if ($app.pendingApplicationSubmission) {
    $pendingId = $app.pendingApplicationSubmission.id
    Write-Host "deleting existing pending submission $pendingId"
    Invoke-RestMethod -Method Delete -Uri "$apiRoot/submissions/$pendingId" -Headers $headers | Out-Null
}

# -- 3. create ------------------------------------------------------------------------------------
$submission   = Invoke-RestMethod -Method Post -Uri "$apiRoot/submissions" -Headers $headers -ContentType "application/json"
$submissionId = $submission.id
Write-Host "created submission $submissionId"

# -- 4. swap the packages -------------------------------------------------------------------------
# Retire whatever the cloned submission carried over, then add ours. fileName is the path INSIDE the
# zip uploaded in step 5, so the two must agree exactly.
foreach ($pkg in $submission.applicationPackages) { $pkg.fileStatus = "PendingDelete" }
$submission.applicationPackages += [pscustomobject]@{
    fileName   = $msix.Name
    fileStatus = "PendingUpload"
}

Invoke-RestMethod -Method Put -Uri "$apiRoot/submissions/$submissionId" -Headers $headers `
    -ContentType "application/json" -Body ($submission | ConvertTo-Json -Depth 60) | Out-Null
Write-Host "package list updated"

# -- 5. upload ------------------------------------------------------------------------------------
$zipPath = Join-Path ([System.IO.Path]::GetTempPath()) ("evedeck-store-" + [guid]::NewGuid().ToString("N") + ".zip")
try {
    Compress-Archive -Path $msix.FullName -DestinationPath $zipPath -CompressionLevel Optimal -Force
    $zip = Get-Item $zipPath
    Write-Host "uploading $([math]::Round($zip.Length / 1MB, 1)) MB"

    # The SAS URL carries its own auth, so the bearer token must NOT be sent here.
    Invoke-RestMethod -Method Put -Uri $submission.fileUploadUrl.Replace("+", "%2B") `
        -Headers @{ "x-ms-blob-type" = "BlockBlob" } -InFile $zip.FullName | Out-Null
    Write-Host "uploaded"
}
finally {
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
}

if (-not $Commit) {
    Write-Host ""
    Write-Host "DRAFT ONLY. Nothing has been submitted for certification."
    Write-Host "Review it in Partner Center, then re-run with -Commit to submit."
    Write-Host "Submission id: $submissionId"
    return
}

# -- 6. commit and poll ---------------------------------------------------------------------------
Invoke-RestMethod -Method Post -Uri "$apiRoot/submissions/$submissionId/commit" -Headers $headers -ContentType "application/json" | Out-Null
Write-Host "committed; waiting for the Store to accept it"

# Only waits for the Store to finish INGESTING the package. Certification itself runs for hours to
# days afterwards, so this deliberately stops at CommitStarted clearing rather than pretending to
# track the whole review.
do {
    Start-Sleep -Seconds 15
    $status = Invoke-RestMethod -Method Get -Uri "$apiRoot/submissions/$submissionId/status" -Headers $headers
    Write-Host "  status: $($status.status)"
} while ($status.status -eq "CommitStarted")

if ($status.status -eq "CommitFailed") {
    $status.statusDetails.errors | ForEach-Object { Write-Host "  ERROR $($_.code): $($_.details)" }
    throw "The Store rejected the submission (status: $($status.status))."
}

Write-Host ""
Write-Host "Submitted. Status: $($status.status). Certification continues in Partner Center."
