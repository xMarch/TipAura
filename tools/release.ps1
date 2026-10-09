<#
.SYNOPSIS
Builds a TipAura release from main and records it as one squashed commit on the public branch.

.DESCRIPTION
Reads <Version> from TipAura.csproj, publishes the Native AOT build with TipAuraRelease=true (About shows the
plain version instead of "-dev"), zips it, then creates a commit on the public branch whose tree is the
current main tree minus $Exclude. The commit's only parent is the previous public release, so development
history and commit messages stay local. The commit is tagged v<Version>. Nothing is pushed and no GitHub
release is created. The release commit and tag use the GitHub noreply address ($PublicEmail) so pushing
them neither exposes nor is blocked by the account's private email.

.EXAMPLE
./tools/release.ps1 -WhatIf      # checks and shows what would be created
./tools/release.ps1              # build, zip, commit and tag
#>
[CmdletBinding()]
param(
    [switch]$WhatIf,
    [switch]$SkipBuild,
    [string]$PublicBranch = 'public',
    # Tracked paths left out of the public tree.
    [string[]]$Exclude = @('imgui.ini'),
    # Author, committer and tagger address for the published objects.
    [string]$PublicEmail = '13718273+xMarch@users.noreply.github.com'
)

$ErrorActionPreference = 'Stop'

function Invoke-Git {
    $output = & git.exe @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed with exit code $LASTEXITCODE." }
    $output
}

function Get-GitOrNull {
    $ErrorActionPreference = 'Continue'
    $output = & git.exe @args 2>$null
    if ($LASTEXITCODE -eq 0) { $output } else { $null }
}

$root = (Invoke-Git rev-parse --show-toplevel).Trim()
Set-Location $root

$branch = (Invoke-Git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { throw "Release from main (current branch: $branch)." }
if (Invoke-Git status --porcelain) { throw 'The working tree has uncommitted changes; commit or discard them first.' }

$version = (& dotnet msbuild TipAura.csproj -nologo -getProperty:Version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not read <Version> from TipAura.csproj.' }
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$version' is not a semantic version." }
$tag = "v$version"
if (Get-GitOrNull rev-parse -q --verify "refs/tags/$tag") { throw "Tag $tag already exists; bump <Version> in TipAura.csproj." }
if ($PublicEmail -notmatch '@users\.noreply\.github\.com$') { throw "PublicEmail '$PublicEmail' is not a GitHub noreply address." }
$publicName = (Invoke-Git config user.name).Trim()

# Build the public tree in a temporary index so the working tree and the real index stay untouched.
$index = Join-Path ([IO.Path]::GetTempPath()) "tipaura-release-$([guid]::NewGuid().ToString('N')).index"
$previousIndex = $env:GIT_INDEX_FILE
try {
    $env:GIT_INDEX_FILE = $index
    Invoke-Git read-tree HEAD | Out-Null
    if ($Exclude.Count -gt 0) { Invoke-Git rm --cached -q --ignore-unmatch -- @Exclude | Out-Null }
    $tree = (Invoke-Git write-tree).Trim()
}
finally {
    $env:GIT_INDEX_FILE = $previousIndex
    Remove-Item -LiteralPath $index -ErrorAction SilentlyContinue
}

$parent = Get-GitOrNull rev-parse -q --verify "refs/heads/$PublicBranch"
if ($parent) {
    $parent = $parent.Trim()
    if ((Invoke-Git rev-parse "$parent^{tree}").Trim() -eq $tree) { throw "No changes since the last release on $PublicBranch." }
}

$outDir = Join-Path $root "artifacts/release/$tag"
$zip = Join-Path $root "artifacts/release/tipaura-$tag-win-x64.zip"
$head = (Invoke-Git rev-parse --short HEAD).Trim()
Write-Host "Version:  $version (tag $tag)"
Write-Host "Source:   main @ $head; excluded: $($Exclude -join ', ')"
Write-Host "Public:   $PublicBranch $(if ($parent) { "@ $($parent.Substring(0, 7))" } else { '(new orphan branch)' }) -> tree $($tree.Substring(0, 7))"
Write-Host "Package:  $zip"
Write-Host "Identity: $publicName <$PublicEmail>"
if ($WhatIf) { Write-Host 'WhatIf: nothing was built, committed or tagged.'; return }

# Build before touching refs, so a failed build leaves no commit or tag behind.
if (-not $SkipBuild) {
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    & dotnet publish TipAura.csproj -c Release -r win-x64 -p:PublishAot=true -p:TipAuraRelease=true -o $outDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    Copy-Item LICENSE (Join-Path $outDir 'LICENSE.txt')
    $licenses = New-Item -ItemType Directory -Force (Join-Path $outDir 'licenses')
    Copy-Item assets/licenses/*.txt $licenses
    Copy-Item assets/fonts/LICENSE-lucide.txt (Join-Path $licenses 'lucide.txt')
    if (Test-Path $zip) { Remove-Item -Force $zip }
    $files = Get-ChildItem $outDir | Where-Object { $_.Extension -ne '.pdb' }
    Compress-Archive -Path $files.FullName -DestinationPath $zip
}

$message = "Release $tag"
$commitArgs = @('commit-tree', $tree, '-m', $message)
if ($parent) { $commitArgs += @('-p', $parent) }
# The tagger identity comes from the committer variables.
$identity = @{
    GIT_AUTHOR_NAME = $publicName; GIT_AUTHOR_EMAIL = $PublicEmail
    GIT_COMMITTER_NAME = $publicName; GIT_COMMITTER_EMAIL = $PublicEmail
}
$previousIdentity = @{}
foreach ($name in $identity.Keys) {
    $previousIdentity[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $identity[$name])
}
try {
    $commit = (Invoke-Git @commitArgs).Trim()
    # The expected old value guards against the branch moving meanwhile; all zeros means "must not exist yet".
    $expected = if ($parent) { $parent } else { '0' * 40 }
    Invoke-Git update-ref "refs/heads/$PublicBranch" $commit $expected | Out-Null
    Invoke-Git tag -a $tag $commit -m "TipAura $version" | Out-Null
}
finally {
    foreach ($name in $identity.Keys) { [Environment]::SetEnvironmentVariable($name, $previousIdentity[$name]) }
}

Write-Host ""
Write-Host "Created $PublicBranch @ $($commit.Substring(0, 7)) and tag $tag."
Write-Host 'Next steps (manual):'
Write-Host "  git push origin ${PublicBranch}:main"
Write-Host "  git push origin $tag"
Write-Host "  Create the GitHub release for $tag and upload $zip"
