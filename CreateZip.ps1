param(
    [string]$TargetPath,
    [string]$ProjectDir
)

$ProjectDir = $ProjectDir.TrimEnd('\', '/')

$staging = Join-Path $env:TEMP "SailwindVirtualCrewZipStaging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path "$staging\SailwindVirtualCrew\Sounds" | Out-Null

Copy-Item $TargetPath "$staging\SailwindVirtualCrew\"
Copy-Item "$ProjectDir\audio\shipbell.wav" "$staging\SailwindVirtualCrew\Sounds\shipbell.wav"

$zipPath = "$ProjectDir\bin\Release\SailwindVirtualCrew.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Compress-Archive -Path "$staging\SailwindVirtualCrew" -DestinationPath $zipPath
Remove-Item $staging -Recurse -Force

Write-Host "Created release zip: $zipPath"
