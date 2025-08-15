# --- 配置参数 ---
$PowerPlans = @{
    "高性能" = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    "节能"   = "a1841308-3541-4fab-bc81-f71556f20b4a"
}
$HighPerfPlanName = "高性能"
$PowerSaverPlanName = "节能"

# --- 电源设置 GUID (固定值) ---
$SubgroupProc = "54533251-82be-4824-96c1-47b60b740d00" # 处理器电源管理 GUID
$MinProcStateGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c" # 最小处理器状态
$MaxProcStateGuid = "bc5038f7-23e0-4960-96da-33abaf5935ec" # 最大处理器状态

# --- 阈值与权重 ---
$HighLoadThreshold = 60.0
$HighLoadDurationS = 6
$LowLoadThreshold = 30.0
$LowLoadDurationS = 180
$PollingIntervalS = 1
$ImmediateSwitch_Usage = 90.0
$HighPerfTier1_Usage   = 80.0; $HighPerfTier1_Weight = 2.0
$ExtremeLowLoad_Usage  = 10.0; $ExtremeLowLoad_Weight = 4.0
$NormalLowLoad_Weight  = 2.0

# --- 函数定义 ---

function Get-CombinedCpuUsage {
    $result = [PSCustomObject]@{ HostUsage = 0.0; VcpuMaxUsage = 0.0; VcpuSource = "无运行中的VM" }
    try {
        $hostSample = (Get-Counter -Counter "\Processor(_Total)\% Processor Time" -ErrorAction Stop).CounterSamples
        if ($hostSample) { $result.HostUsage = [math]::Round($hostSample.CookedValue, 2) }
    } catch { Write-Warning "无法获取宿主机CPU使用率: $($_.Exception.Message)" }
    try {
        $vcpuSamples = (Get-Counter -Counter "\Hyper-V Hypervisor Virtual Processor(*:*)\% Guest Run Time" -ErrorAction Stop).CounterSamples
        if ($vcpuSamples) {
            $maxVcpuSample = $vcpuSamples | Sort-Object -Property CookedValue -Descending | Select-Object -First 1
            if ($maxVcpuSample) {
                $result.VcpuMaxUsage = [math]::Round($maxVcpuSample.CookedValue, 2)
                $instanceName = $maxVcpuSample.InstanceName
                if ($instanceName -match '^(.*?):\s*Virtual\s*Processor\s*(\d+)$') { $result.VcpuSource = "$($Matches[1]):Core-$($Matches[2])" } 
                else { $result.VcpuSource = $instanceName }
            }
        }
    } catch { } # 预期中的失败，无需处理
    return $result
}

function Get-CurrentHostPowerPlan {
    try {
        $activeScheme = powercfg /getactivescheme
        foreach ($planName in $PowerPlans.Keys) { if ($activeScheme -match "\($planName\)") { return $planName } }
        return $PowerSaverPlanName
    } catch { return $PowerSaverPlanName }
}

# [已更新] 切换电源计划并强制设定CPU状态
function Set-HostPowerPlan {
    param([string]$TargetPlanName, [string]$CurrentPlanName)
    if ($TargetPlanName -eq $CurrentPlanName) { return }
    $guid = $PowerPlans[$TargetPlanName]
    if (-not $guid) { return }

    try {
        Write-Host "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ---> [命令] 切换从 '$($CurrentPlanName)' 到 '$($TargetPlanName)'" -ForegroundColor Yellow
        powercfg /setactive $guid *>&1 | Out-Null

        # 切换成功后，根据目标计划强制设定处理器状态
        if ($TargetPlanName -eq $HighPerfPlanName) {
            Write-Host "       ---> [配置] 设定 '$($TargetPlanName)' 处理器状态为 100% (最小) / 100% (最大)" -ForegroundColor DarkGray
            powercfg /setacvalueindex $guid $SubgroupProc $MinProcStateGuid 100 *>&1 | Out-Null
            powercfg /setacvalueindex $guid $SubgroupProc $MaxProcStateGuid 100 *>&1 | Out-Null
            powercfg /setdcvalueindex $guid $SubgroupProc $MinProcStateGuid 100 *>&1 | Out-Null # 电池模式
            powercfg /setdcvalueindex $guid $SubgroupProc $MaxProcStateGuid 100 *>&1 | Out-Null # 电池模式
        }
        elseif ($TargetPlanName -eq $PowerSaverPlanName) {
            Write-Host "       ---> [配置] 设定 '$($TargetPlanName)' 处理器状态为 1% (最小) / 1% (最大)" -ForegroundColor DarkGray
            powercfg /setacvalueindex $guid $SubgroupProc $MinProcStateGuid 1 *>&1 | Out-Null
            powercfg /setacvalueindex $guid $SubgroupProc $MaxProcStateGuid 1 *>&1 | Out-Null
            powercfg /setdcvalueindex $guid $SubgroupProc $MinProcStateGuid 1 *>&1 | Out-Null # 电池模式
            powercfg /setdcvalueindex $guid $SubgroupProc $MaxProcStateGuid 1 *>&1 | Out-Null # 电池模式
        }
    } catch { 
        Write-Error "切换或配置电源计划 '$($TargetPlanName)' 失败。" 
    }
}

# --- 主程序 ---
Write-Host "--- Hyper-V 极简电源调度器 v13.0 (CPU状态动态配置版) ---" -ForegroundColor Green
$currentUser = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentUser.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "错误：本脚本需要管理员权限才能运行。"; Read-Host "按 Enter 键退出..."; exit 1
}
Write-Host "监控对象: 所有虚拟机 vCPU 及宿主机 CPU"
Write-Host "按 Ctrl+C 停止程序，并恢复到“节能”模式(1%/1% CPU)。"
$highLoadAccumulatedS = 0.0
$lowLoadAccumulatedS = 0.0
$lastLoopTimestamp = Get-Date
$initialPlan = Get-CurrentHostPowerPlan
$isSwitching = $false
$targetPlan = $null
Write-Host "检测到初始电源计划: $initialPlan"
Write-Host "节能降级冷却时长: $($LowLoadDurationS)s"
Write-Host ("-" * 80)

try {
    while ($true) {
        $currentLoopTimestamp = Get-Date
        $deltaTimeS = ($currentLoopTimestamp - $lastLoopTimestamp).TotalSeconds
        $lastLoopTimestamp = $currentLoopTimestamp

        $currentPlan = Get-CurrentHostPowerPlan
        
        $cpuStats = Get-CombinedCpuUsage
        $effectiveUsage = [math]::Max($cpuStats.VcpuMaxUsage, $cpuStats.HostUsage)
        
        if ($isSwitching) {
            $statusMessage = "状态: 正在切换到 '$($targetPlan)'，等待系统确认..."
            if ($currentPlan -eq $targetPlan) {
                $statusMessage = "状态: 已确认切换到 '$($targetPlan)'，恢复监控。"
                $isSwitching = $false
                $targetPlan = $null
            }
        }
        elseif ($effectiveUsage -gt $HighLoadThreshold) {
            $lowLoadAccumulatedS = 0.0
            if ($currentPlan -ne $HighPerfPlanName) {
                $doSwitch = $false
                if ($effectiveUsage -gt $ImmediateSwitch_Usage) {
                    $statusMessage = "状态: 负载紧急(>90%)，准备切换！"
                    $doSwitch = $true
                } else {
                    $weight = 1.0; if ($effectiveUsage -gt $HighPerfTier1_Usage) { $weight = $HighPerfTier1_Weight }
                    $highLoadAccumulatedS += $deltaTimeS * $weight
                    if ($highLoadAccumulatedS -ge $HighLoadDurationS) { $doSwitch = $true }
                    $statusMessage = "状态: 高性能升级检测... [{0:N1}s / {1}s] (压力: {2}%, 权重: {3}x)" -f $highLoadAccumulatedS, $HighLoadDurationS, $effectiveUsage, $weight
                }
                if ($doSwitch) {
                    Set-HostPowerPlan -TargetPlanName $HighPerfPlanName -CurrentPlanName $currentPlan
                    $highLoadAccumulatedS = 0.0; $isSwitching = $true; $targetPlan = $HighPerfPlanName
                }
            } else {
                $highLoadAccumulatedS = 0.0
                $statusMessage = "状态: 负载持续高位({0}%)，保持高性能" -f $effectiveUsage
            }
        }
        else {
            $highLoadAccumulatedS = 0.0
            if ($currentPlan -eq $HighPerfPlanName) {
                $isCoolingDown = ($effectiveUsage -lt $LowLoadThreshold) -or ($lowLoadAccumulatedS -gt 0)
                if ($isCoolingDown) {
                    $weight = 1.0; if ($effectiveUsage -lt $ExtremeLowLoad_Usage) { $weight = $ExtremeLowLoad_Weight } elseif ($effectiveUsage -lt $LowLoadThreshold) { $weight = $NormalLowLoad_Weight }
                    $lowLoadAccumulatedS += $deltaTimeS * $weight
                    if ($lowLoadAccumulatedS -ge $LowLoadDurationS) {
                        Set-HostPowerPlan -TargetPlanName $PowerSaverPlanName -CurrentPlanName $currentPlan
                        $lowLoadAccumulatedS = 0.0; $isSwitching = $true; $targetPlan = $PowerSaverPlanName
                    } else {
                        $statusMessage = "状态: 节能模式降级检测... [{0:N1}s / {1}s] (负载: {2}%, 权重: {3}x)" -f $lowLoadAccumulatedS, $LowLoadDurationS, $effectiveUsage, $weight
                    }
                } else { $statusMessage = "状态: 负载平稳({0}%)，持续监控中..." -f $effectiveUsage }
            } else {
                $lowLoadAccumulatedS = 0.0
                $statusMessage = "状态: 负载平稳({0}%)，持续监控中..." -f $effectiveUsage
            }
        }
        $logLine = @(
            "$(Get-Date -Format 'HH:mm:ss')", "| 模式: $($currentPlan.PadRight(10))",
            "| vCPU: $('{0,6:F2}' -f $cpuStats.VcpuMaxUsage) %", "| 宿主机: $('{0,6:F2}' -f $cpuStats.HostUsage) %",
            "| vCPU源: $($cpuStats.VcpuSource.PadRight(28))", "| $($statusMessage)"
        ) -join ' '
        Write-Host $logLine
        
        Start-Sleep -Seconds $PollingIntervalS
    }
}
finally {
    Write-Host "`n"; Write-Host "脚本正在停止... 正在恢复到“节能”模式(1%/1% CPU)..." -ForegroundColor Yellow
    $finalPlan = Get-CurrentHostPowerPlan
    Set-HostPowerPlan -TargetPlanName $PowerSaverPlanName -CurrentPlanName $finalPlan
    Write-Host "已恢复到“节能”模式。程序退出。" -ForegroundColor Green
}