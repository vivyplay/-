param(
    [Parameter(Mandatory = $true)][string]$OldInstaller,
    [Parameter(Mandatory = $true)][string]$NewInstaller,
    [Parameter(Mandatory = $true)][string]$TestRunner,
    [Parameter(Mandatory = $true)][string]$TestsRoot
)

$ErrorActionPreference = 'Stop'
$results = [System.Collections.Generic.List[object]]::new()
$script:failed = $false
function Record([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $results.Add([pscustomobject]@{ test = $Name; passed = $Passed; detail = $Detail })
    if (-not $Passed) { $script:failed = $true }
    Write-Output "$(if ($Passed) { 'PASS' } else { 'FAIL' }) $Name $Detail"
}
function Get-DataFingerprint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 'absent' }
    $items = Get-ChildItem -LiteralPath $Path -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Path.Length).TrimStart('\')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$relative|$($_.Length)|$hash"
    }
    return ($items -join "`n")
}

$work = Join-Path $TestsRoot ("installer-validation-" + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $work 'app'
$smokeData = Join-Path $work 'isolated-app-data'
$externalDirectory = Join-Path $work 'user-selected-files'
$externalSentinel = Join-Path $externalDirectory 'keep-me.txt'
$foreignFile = Join-Path $app 'unrelated-user-file.txt'
$runPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runPath, $false)
$hadStartup = $null -ne $runKey.GetValue('Suisheng', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
$startupValue = $null
$startupKind = $null
if ($hadStartup) {
    $startupValue = $runKey.GetValue('Suisheng', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $startupKind = $runKey.GetValueKind('Suisheng')
}
$runKey.Close()
$userData = Join-Path $env:LOCALAPPDATA 'Suisheng'
$dataBefore = Get-DataFingerprint $userData
$startupCleanupVerified = $false
$startupRestored = $false
New-Item -ItemType Directory -Path $app, $smokeData, $externalDirectory -Force | Out-Null
Set-Content -LiteralPath $externalSentinel -Value 'user-owned file; installer must not touch' -Encoding UTF8

try {
    $oldArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCANCEL', '/NOICONS', "/DIR=`"$app`"", "/LOG=`"$work\install-old.log`"")
    $process = Start-Process -FilePath $OldInstaller -ArgumentList $oldArguments -Wait -PassThru
    $installedExe = Join-Path $app 'Suisheng.exe'
    $oldInstalled = $process.ExitCode -eq 0 -and (Test-Path -LiteralPath $installedExe) -and (Get-Item -LiteralPath $installedExe).VersionInfo.FileVersion -eq '0.3.1.0'
    Record '隔离目录安装 0.3.1' $oldInstalled "退出码=$($process.ExitCode)"
    if (-not $oldInstalled) { throw '旧版安装失败，无法继续升级测试。' }

    Set-Content -LiteralPath $foreignFile -Value 'not owned by the installer' -Encoding UTF8
    $newArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCANCEL', '/NOICONS', "/DIR=`"$app`"", "/LOG=`"$work\upgrade-040.log`"")
    $process = Start-Process -FilePath $NewInstaller -ArgumentList $newArguments -Wait -PassThru
    $upgraded = $process.ExitCode -eq 0 -and (Get-Item -LiteralPath $installedExe).VersionInfo.FileVersion -eq '0.4.0.0'
    $preservedFiles = (Get-Content -LiteralPath $foreignFile -Raw).Trim() -eq 'not owned by the installer' -and (Test-Path -LiteralPath $externalSentinel)
    Record '0.3.1 覆盖升级为 0.4.0' ($upgraded -and $preservedFiles) "退出码=$($process.ExitCode)；保留无关文件=$preservedFiles"
    if (-not $upgraded) { throw '新版升级失败。' }

    $previousDataOverride = $env:SUISHENG_DATA_DIRECTORY
    try {
        $env:SUISHENG_DATA_DIRECTORY = $smokeData
        & $TestRunner $TestsRoot --desktop --executable $installedExe
        $smokeExit = $LASTEXITCODE
    }
    finally {
        $env:SUISHENG_DATA_DIRECTORY = $previousDataOverride
    }
    $desktopReport = Get-Content -LiteralPath (Join-Path $TestsRoot 'desktop-release-results.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $desktopPassed = $smokeExit -eq 0 -and @($desktopReport | Where-Object { -not $_.passed }).Count -eq 0
    Copy-Item -LiteralPath (Join-Path $TestsRoot 'desktop-release-results.json') -Destination (Join-Path $TestsRoot 'installed-desktop-results.json') -Force
    Record '已安装 0.4.0 完整桌面回归' $desktopPassed "断言=$(@($desktopReport).Count)；退出码=$smokeExit"

    $uninstaller = Join-Path $app 'unins000.exe'
    $process = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runPath, $false)
    $startupAfterUninstall = $runKey.GetValue('Suisheng', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $runKey.Close()
    $startupCleanupVerified = $null -eq $startupAfterUninstall
    $dataAfter = Get-DataFingerprint $userData
    $dataPreserved = $dataAfter -eq $dataBefore
    $uninstalled = $process.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $installedExe)
    $otherFilesPreserved = (Test-Path -LiteralPath $foreignFile) -and (Test-Path -LiteralPath $externalSentinel)
    Record '卸载程序文件并清理本软件启动项' ($uninstalled -and $startupCleanupVerified) "退出码=$($process.ExitCode)；启动项已清理=$startupCleanupVerified"
    Record '卸载默认保留现有用户数据且不删其他文件' ($dataPreserved -and $otherFilesPreserved) "用户数据未变化=$dataPreserved；其他文件保留=$otherFilesPreserved"
}
catch {
    Record '安装升级卸载测试运行器' false $_.Exception.GetType().FullName
    $uninstaller = Join-Path $app 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait
    }
}
finally {
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runPath)
    if ($hadStartup) {
        $runKey.SetValue('Suisheng', $startupValue, $startupKind)
    }
    else {
        $runKey.DeleteValue('Suisheng', $false)
    }
    $runKey.Close()
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runPath, $false)
    if ($hadStartup) {
        $current = $runKey.GetValue('Suisheng', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $startupRestored = $current -eq $startupValue -and $runKey.GetValueKind('Suisheng') -eq $startupKind
    }
    else {
        $startupRestored = $null -eq $runKey.GetValue('Suisheng', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
    $runKey.Close()
    Record '测试后恢复原开机启动项' $startupRestored "原启动项存在=$hadStartup"
    $results.Add([pscustomobject]@{ test = '卸载时主动选择删除用户数据'; passed = $null; status = 'unverified'; detail = '为避免删除真实设置、模型和 DPAPI 密钥，本次只执行保留分支；删除分支仅做脚本静态检查。' })
    $reportPath = Join-Path $TestsRoot 'installer-results.json'
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}

if ($script:failed) { exit 1 }
exit 0

