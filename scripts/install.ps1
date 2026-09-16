<#
.SYNOPSIS
    Install terminalfs from a GitHub release.

.DESCRIPTION
    irm https://raw.githubusercontent.com/petar-stupar/terminalfs/main/scripts/install.ps1 | iex

    Nothing here needs an elevated prompt unless -BinDir points somewhere that does.

.PARAMETER Version
    A particular release, such as v0.1.0. The latest by default.

.PARAMETER BinDir
    Where to put the binary. %LOCALAPPDATA%\Programs\terminalfs by default.
#>
[CmdletBinding()]
param(
    [string] $Version = 'latest',
    [string] $BinDir = (Join-Path $env:LOCALAPPDATA 'Programs\terminalfs')
)

$ErrorActionPreference = 'Stop'
$repo = 'petar-stupar/terminalfs'

$arch = switch ($env:PROCESSOR_ARCHITECTURE) {
    'AMD64' { 'x64' }
    'ARM64' { 'arm64' }
    default { throw "terminalfs has no build for $env:PROCESSOR_ARCHITECTURE" }
}

if ($Version -eq 'latest') {
    $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
    $Version = $release.tag_name
    if (-not $Version) { throw "could not find the latest release of $repo" }
}

$tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
$bare = $tag.TrimStart('v')

$archive = "terminalfs-$bare-win-$arch.zip"
$base = "https://github.com/$repo/releases/download/$tag"

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
    Write-Host "install.ps1: fetching $archive"
    Invoke-WebRequest "$base/$archive" -OutFile (Join-Path $tmp $archive)
    Invoke-WebRequest "$base/SHA256SUMS" -OutFile (Join-Path $tmp 'SHA256SUMS')

    $expected = (Get-Content (Join-Path $tmp 'SHA256SUMS') |
        Where-Object { $_ -match "\s$([regex]::Escape($archive))$" } |
        ForEach-Object { ($_ -split '\s+')[0] } |
        Select-Object -First 1)

    if (-not $expected) { throw "SHA256SUMS does not mention $archive" }

    $actual = (Get-FileHash (Join-Path $tmp $archive) -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expected.ToLower()) {
        throw "checksum mismatch for ${archive}: expected $expected, got $actual"
    }

    Write-Host 'install.ps1: checksum ok'

    Expand-Archive -Path (Join-Path $tmp $archive) -DestinationPath $tmp -Force
    $binary = Join-Path $tmp 'terminalfs.exe'
    if (-not (Test-Path $binary)) { throw "$archive does not contain terminalfs.exe" }

    New-Item -ItemType Directory -Path $BinDir -Force | Out-Null
    Copy-Item $binary (Join-Path $BinDir 'terminalfs.exe') -Force

    Write-Host "install.ps1: installed $tag to $(Join-Path $BinDir 'terminalfs.exe')"

    # The user's own PATH, not the process copy, which is what a later shell will read.
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath -split ';' -notcontains $BinDir) {
        Write-Host "install.ps1: $BinDir is not on your PATH. Add it for future shells with:"
        Write-Host "    [Environment]::SetEnvironmentVariable('Path', `"`$env:Path;$BinDir`", 'User')"
    } else {
        Write-Host "install.ps1: run 'terminalfs --help'"
    }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
