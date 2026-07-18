param(
  [string]$Version,
  [ValidateSet("win-x64", "win-arm64")]
  [string]$Runtime = "win-x64",
  [ValidateSet("all", "self-contained", "slim")]
  [string]$Package = "all"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "MiniFences\MiniFences.csproj"
$localDotnet = Join-Path $repositoryRoot ".dotnet\dotnet.exe"
$legacyBundledDotnet = Join-Path $repositoryRoot ".dotnet-sdk\dotnet.exe"
$dotnet = if (Test-Path $localDotnet) { $localDotnet } elseif (Test-Path $legacyBundledDotnet) { $legacyBundledDotnet } else { "dotnet" }

if ([string]::IsNullOrWhiteSpace($Version)) {
  [xml]$project = Get-Content -LiteralPath $projectPath
  $versionPropertyGroup = $project.Project.PropertyGroup |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Version) } |
    Select-Object -First 1
  $Version = [string]$versionPropertyGroup.Version
}
if ([string]::IsNullOrWhiteSpace($Version)) {
  throw "The project does not define a release version: $projectPath"
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
  throw "The release version must contain three numeric parts: $Version"
}

$packages = @()
if ($Package -eq "all" -or $Package -eq "self-contained") {
  $packages += [pscustomobject]@{
    Name = "MiniFences-$Runtime-$Version"
    SelfContained = $true
    Description = "Self-contained"
  }
}
if ($Package -eq "all" -or $Package -eq "slim") {
  $packages += [pscustomobject]@{
    Name = "MiniFences-$Runtime-$Version-slim"
    SelfContained = $false
    Description = "Framework-dependent"
  }
}

foreach ($release in $packages) {
  $outputPath = Join-Path $repositoryRoot "artifacts\$($release.Name)"
  $archivePath = "$outputPath.zip"
  $checksumPath = "$archivePath.sha256"
  if (Test-Path $outputPath) {
    throw "Release output already exists: $outputPath. Choose another version or remove that release folder explicitly."
  }
  if (Test-Path $archivePath) {
    throw "Release archive already exists: $archivePath. Choose another version or remove that archive explicitly."
  }
  if (Test-Path $checksumPath) {
    throw "Release checksum already exists: $checksumPath. Choose another version or remove that checksum explicitly."
  }
}

foreach ($release in $packages) {
  $outputPath = Join-Path $repositoryRoot "artifacts\$($release.Name)"
  $archivePath = "$outputPath.zip"
  $selfContained = $release.SelfContained.ToString().ToLowerInvariant()

  & $dotnet publish $projectPath `
    --configuration Release `
    --runtime $Runtime `
    --self-contained $selfContained `
    --output $outputPath `
    -p:Version=$Version `
    -p:FileVersion="$Version.0" `
    -p:AssemblyVersion="$Version.0"

  if ($LASTEXITCODE -ne 0) {
    throw "MiniFences publish failed with exit code $LASTEXITCODE."
  }

  $executablePath = Join-Path $outputPath "MiniFences.exe"
  if (-not (Test-Path $executablePath)) {
    throw "Publish completed without MiniFences.exe: $outputPath"
  }

  Get-ChildItem $outputPath -Filter "*.pdb" -File | Remove-Item -Force
  Compress-Archive -Path (Join-Path $outputPath "*") -DestinationPath $archivePath -CompressionLevel Optimal
  $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
  Set-Content -LiteralPath "$archivePath.sha256" -Value "$archiveHash  $([IO.Path]::GetFileName($archivePath))" -Encoding ascii

  Write-Host "$($release.Description) MiniFences release created: $executablePath"
  Write-Host "Release archive created: $archivePath"
  Write-Host "SHA-256: $archiveHash"
}
