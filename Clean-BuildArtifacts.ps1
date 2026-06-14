[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param()

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$rootFullPath = [System.IO.Path]::GetFullPath($root)

function Test-IsUnderRoot {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Path
	)

	$fullPath = [System.IO.Path]::GetFullPath($Path)
	return $fullPath.Equals($rootFullPath, [System.StringComparison]::OrdinalIgnoreCase) -or
		$fullPath.StartsWith($rootFullPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
}

$projectDirectories = Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.csproj' |
	Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' } |
	ForEach-Object { $_.Directory.FullName } |
	Sort-Object -Unique

$targets = [System.Collections.Generic.List[string]]::new()

$vsDirectory = Join-Path $root '.vs'
if (Test-Path -LiteralPath $vsDirectory -PathType Container) {
	$targets.Add($vsDirectory)
}

foreach ($projectDirectory in $projectDirectories) {
	foreach ($directoryName in @('bin', 'obj')) {
		$target = Join-Path $projectDirectory $directoryName
		if (Test-Path -LiteralPath $target -PathType Container) {
			$targets.Add($target)
		}
	}
}

foreach ($target in ($targets | Sort-Object -Unique)) {
	if (-not (Test-IsUnderRoot -Path $target)) {
		throw "Refusing to remove path outside repository root: $target"
	}

	if ($PSCmdlet.ShouldProcess($target, 'Remove directory')) {
		Remove-Item -LiteralPath $target -Recurse -Force
	}
}

Write-Host "Processed $($targets.Count) build artifact director$(if ($targets.Count -eq 1) { 'y' } else { 'ies' })."
