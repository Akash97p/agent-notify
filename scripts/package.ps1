[CmdletBinding()]
param(
    [string]$DotnetPath = "dotnet",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifacts = Join-Path $root "artifacts"
$payload = Join-Path $artifacts "payload"
$appOut = Join-Path $artifacts "app-publish"
$cliOut = Join-Path $artifacts "cli-publish"
$setupOut = Join-Path $artifacts "setup-publish"

if ([IO.Path]::GetFileName($root) -ne "agent-notify") {
    throw "Refusing to package from unexpected repository root: $root"
}

foreach ($directory in @($payload, $appOut, $cliOut, $setupOut)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory | Out-Null
}

$publish = @(
    "publish", "--configuration", $Configuration, "--runtime", $Runtime,
    "--self-contained", "true", "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true", "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

function Invoke-Dotnet([string[]]$Arguments) {
    & $DotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE"
    }
}

Push-Location $root
try {
    Invoke-Dotnet ($publish + @("src/AgentNotify.App/AgentNotify.App.csproj", "--output", $appOut))
    Invoke-Dotnet ($publish + @("src/AgentNotify.Cli/AgentNotify.Cli.csproj", "--output", $cliOut))

    Copy-Item (Join-Path $appOut "AgentNotify.Tray.exe") (Join-Path $payload "AgentNotify.Tray.exe")
    Copy-Item (Join-Path $cliOut "agentnotify.exe") (Join-Path $payload "agentnotify.exe")

    Invoke-Dotnet ($publish + @(
        "src/AgentNotify.Setup/AgentNotify.Setup.csproj", "--output", $setupOut,
        "-p:PayloadDir=$payload", "-p:RequirePayload=true"
    ))

    $installer = Join-Path $artifacts "AgentNotifySetup.exe"
    Copy-Item (Join-Path $setupOut "AgentNotifySetup.exe") $installer -Force

    $skillPath = Join-Path $root "distribution/agentnotify/SKILL.md"
    $skill = Get-Content -LiteralPath $skillPath -Raw
    if ($skill -notmatch "(?m)^name:\s+agentnotify\s*$" -or
        $skill -notmatch "(?m)^description:\s+\S") {
        throw "The distributable SKILL.md has invalid required frontmatter."
    }

    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $artifacts "SHA256SUMS.txt") -Value "$hash *AgentNotifySetup.exe" -Encoding ascii

    Write-Host "Created $installer"
    Write-Host "SHA-256: $hash"
}
finally {
    Pop-Location
}

