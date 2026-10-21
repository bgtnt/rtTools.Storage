$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$verificationId = 'verify-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$verificationRoot = Join-Path $PSScriptRoot ('artifacts\' + $verificationId)
$buildArtifacts = Join-Path $verificationRoot 'build'
$feed = Join-Path $verificationRoot 'feed'
$packages = Join-Path $verificationRoot 'packages'
$runData = Join-Path $PSScriptRoot ('run-data\' + $verificationId)
foreach ($directory in @($verificationRoot, $feed, $packages, $runData)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
$library = Join-Path $PSScriptRoot 'src\RtTools.Persistent\RtTools.Persistent.csproj'
$checks = Join-Path $PSScriptRoot 'checks\ContractChecks\ContractChecks.csproj'
$sample = Join-Path $PSScriptRoot 'samples\ResidentState\ResidentState.csproj'
$historyChecks = Join-Path $PSScriptRoot 'checks\HistoryPackageChecks\HistoryPackageChecks.csproj'
$config = Join-Path $PSScriptRoot 'NuGet.Config'
$historyReport = Join-Path $verificationRoot 'history-package.json'
$steps = [Collections.Generic.List[object]]::new()
$failure = $null
$passed = $false
$package = $null

function Invoke-VerificationStep([string]$Name, [string[]]$Arguments) {
    $log = Join-Path $verificationRoot ($Name + '.log')
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath $log | Out-Host
    $code = $LASTEXITCODE
    $steps.Add([pscustomobject]@{ Name=$Name; Arguments=$Arguments; ExitCode=$code; Log=$log })
    if ($code -ne 0) { throw "$Name failed with exit code $code" }
}

try {
    # Build and pack only the local public library. Consumer projects below have
    # exact PackageReferences and receive the new local feed as their sole source.
    Invoke-VerificationStep 'library-restore' @('restore', $library, '--artifacts-path', $buildArtifacts, '--packages', $packages, '--configfile', $config)
    Invoke-VerificationStep 'library-build' @('build', $library, '--no-restore', '--configuration', 'Release', '--artifacts-path', $buildArtifacts, '/p:UseSharedCompilation=false')
    Invoke-VerificationStep 'library-pack' @('pack', $library, '--no-build', '--no-restore', '--configuration', 'Release', '--artifacts-path', $buildArtifacts, '--output', $feed)
    $packagePath = Join-Path $feed 'rtTools.Storage.0.1.0-preview.3.nupkg'
    $packageItem = Get-Item -LiteralPath $packagePath
    $package = [pscustomobject]@{ Path=$packageItem.FullName; Bytes=$packageItem.Length; Sha256=(Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash }

    foreach ($consumer in @(
        @{ Name='state-checks'; Project=$checks; Assembly='ContractChecks'; Arguments=@($runData) },
        @{ Name='sample'; Project=$sample; Assembly='ResidentState'; Arguments=@($runData) },
        @{ Name='history-checks'; Project=$historyChecks; Assembly='HistoryPackageChecks'; Arguments=@((Join-Path $runData 'history-package'), $historyReport) }
    )) {
        Invoke-VerificationStep ($consumer.Name + '-restore') @('restore', $consumer.Project, '--artifacts-path', $buildArtifacts, '--packages', $packages, '--configfile', $config, '--source', $feed)
        Invoke-VerificationStep ($consumer.Name + '-build') @('build', $consumer.Project, '--no-restore', '--configuration', 'Release', '--artifacts-path', $buildArtifacts, '/p:UseSharedCompilation=false')
        $executable = Join-Path $buildArtifacts ('bin\' + $consumer.Assembly + '\release\' + $consumer.Assembly + '.dll')
        Invoke-VerificationStep ($consumer.Name + '-run') (@($executable) + $consumer.Arguments)
    }
    $history = Get-Content -LiteralPath $historyReport -Raw | ConvertFrom-Json
    if ($history.Status -ne 'PASS' -or $history.Mode -ne 'package-history-contracts') { throw 'The package History report did not pass or has the wrong mode.' }
    $requiredSuites = @('acceptance-gate', 'timelog', 'timelog-utc', 'timelog-null', 'log', 'timelog-flush', 'resource-lifetime', 'stream-persistence', 'buffered-queue', 'dictionary-logger', 'timelog-client-errors', 'collection-lifetime', 'storage-bounds')
    $completedSuites = @($history.CompletedSuites | Where-Object { $null -ne $_ })
    if ($completedSuites.Count -ne $requiredSuites.Count -or @($requiredSuites | Where-Object { $_ -notin $completedSuites }).Count -ne 0) {
        throw 'The package History report is incomplete: a required suite completion is missing.'
    }
    $historyCases = @($history.Cases | Where-Object { $null -ne $_ })
    if ($historyCases.Count -lt ($requiredSuites.Count + 1) -or @($historyCases | Where-Object { $_.Status -eq 'FAIL' -or ($_.Kind -eq 'Contract' -and $_.Status -ne 'PASS') }).Count -ne 0) {
        throw 'The package History report has missing, failed, or inconclusive required cases.'
    }
    if ($history.Engine.Identity -notlike 'rtTools.Storage,*' -or
        (Get-FileHash -LiteralPath $history.Engine.Path -Algorithm SHA256).Hash -ne $history.Engine.Sha256) {
        throw 'The package History report does not identify the actual verified package assembly.'
    }
    $passed = $true
}
catch { $failure = $_.Exception.ToString(); Write-Host $failure }
finally {
    [ordered]@{
        Status=if($passed){'PASS'}else{'FAIL'}; RecordedUtc=[DateTimeOffset]::UtcNow
        Package=$package; Steps=$steps; HistoryReport=$historyReport
        ArtifactRoot=$verificationRoot; DataRoot=$runData; Error=$failure
        Limitations=@('Clean local package consumption; no publication is performed', 'Synthetic Flush probes are not physical I/O failure, crash or power-loss tests')
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $verificationRoot 'summary.json') -Encoding utf8
    Write-Output "Verification artifacts: $verificationRoot"
    Write-Output "Verification data: $runData"
}
if (!$passed) { throw "Public package verification failed. See $verificationRoot" }
