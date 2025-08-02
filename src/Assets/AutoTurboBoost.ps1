<#
.SYNOPSIS
    Hyper-V 极简电源调度器 v12.5 (PowerShell Edition - 响应优化版)
.DESCRIPTION
    本脚本持续监控所有虚拟机的 vCPU 负载以及宿主机自身的 CPU 负载，并根据策略在“高性能”和“节能”模式间切换。
    v12.5 版本针对核心性能进行了优化，以解决循环耗时过长的问题：
    - 将两次独立的 Get-Counter 调用合并为一次，大幅减少了每次轮询的延迟。
    - 脚本响应更灵敏，计时更精确，完美解决了“时隙过长”的感觉。
    - 采纳了更长的节能降级冷却时间(180秒)，增强了高负载状态的稳定性。
.VERSION
    12.5
#>

# ==============================================================================
# --- 1. 配置区域 (v12.5 变更) ---
# ==============================================================================
$PowerPlans = @{
    "高性能" = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    "节能"   = "a1841308-3541-4fab-bc81-f71556f20b4a"
}
$HighPerfPlanName = "高性能"
$PowerSaverPlanName = "节能"

# <<< 新增: 将所有需要监控的计数器路径合并到一个数组中
$CounterPaths = @(
    "\Processor(_Total)\% Processor Time",
    "\Hyper-V Hypervisor Virtual Processor(*:*)\% Guest Run Time"
)

# ==============================================================================
# --- 2. 智能调度算法参数 (v12.5 变更) ---
# ==============================================================================
$HighLoadThreshold = 60.0
$HighLoadDurationS = 6
$LowLoadThreshold = 30.0
$LowLoadDurationS = 180 # <<< 变更: 从 120s 延长到 180s
$PollingIntervalS = 1
$ImmediateSwitch_Usage = 90.0
$HighPerfTier1_Usage   = 80.0; $HighPerfTier1_Weight = 2.0
$ExtremeLowLoad_Usage = 10.0; $ExtremeLowLoad_Weight = 4.0
$NormalLowLoad_Weight = 2.0

# ==============================================================================
# --- 3. 核心功能函数 (v12.5 变更) ---
# ==============================================================================

# <<< 重构: 将两个函数合并为一个，一次性获取所有数据，提高效率
function Get-CombinedCpuUsage {
    $result = [PSCustomObject]@{
        HostUsage    = 0.0
        VcpuMaxUsage = 0.0
        VcpuSource   = "无运行中的VM"
    }
    try {
        $allSamples = (Get-Counter -Counter $CounterPaths -ErrorAction Stop).CounterSamples
        if (-not $allSamples) { return $result }

        # 1. 处理宿主机CPU使用率
        $hostSample = $allSamples | Where-Object { $_.Path -like '*\processor(_total)\% processor time' } | Select-Object -First 1
        if ($hostSample) {
            $result.HostUsage = [math]::Round($hostSample.CookedValue, 2)
        }

        # 2. 处理vCPU使用率
        $vcpuSamples = $allSamples | Where-Object { $_.Path -like '*\hyper-v hypervisor virtual processor(*)\% guest run time' }
        if ($vcpuSamples) {
            $maxVcpuSample = $vcpuSamples | Sort-Object -Property CookedValue -Descending | Select-Object -First 1
            if ($maxVcpuSample) {
                $result.VcpuMaxUsage = [math]::Round($maxVcpuSample.CookedValue, 2)
                $instanceName = $maxVcpuSample.InstanceName
                if ($instanceName -match '^(.*?):\s*Virtual\s*Processor\s*(\d+)$') { $result.VcpuSource = "$($Matches[1]):Core-$($Matches[2])" } 
                else { $result.VcpuSource = $instanceName }
            }
        }
    }
    catch {
        $result.VcpuSource = "性能计数器错误"
    }
    return $result
}

function Get-CurrentHostPowerPlan {
    try {
        $activeScheme = powercfg /getactivescheme
        foreach ($planName in $PowerPlans.Keys) { if ($activeScheme -match "\($planName\)") { return $planName } }
        return $PowerSaverPlanName
    } catch { return $PowerSaverPlanName }
}

function Set-HostPowerPlan {
    param([string]$TargetPlanName, [string]$CurrentPlanName)
    if ($TargetPlanName -eq $CurrentPlanName) { return }
    $guid = $PowerPlans[$TargetPlanName]
    if (-not $guid) { return }
    try {
        Write-Host "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ---> [命令] 切换从 '$($CurrentPlanName)' 到 '$($TargetPlanName)'" -ForegroundColor Yellow
        powercfg /setactive $guid *>&1 | Out-Null
    } catch { Write-Error "切换电源计划到 '$($TargetPlanName)' 失败。" }
}

# ==============================================================================
# --- 4. 主程序循环 (v12.5 - 响应优化版) ---
# ==============================================================================
Write-Host "--- Hyper-V 极简电源调度器 v12.5 (响应优化版) ---" -ForegroundColor Green
$currentUser = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentUser.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "错误：本脚本需要管理员权限才能运行。"; Read-Host "按 Enter 键退出..."; exit 1
}
Write-Host "监控对象: 所有虚拟机 vCPU 及宿主机 CPU"
Write-Host "按 Ctrl+C 停止程序，并恢复到“节能”模式。"
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
        
        # --- 获取vCPU和宿主机CPU使用率 (v12.5 优化) ---
        $cpuStats = Get-CombinedCpuUsage
        $vcpuMaxUsage = $cpuStats.VcpuMaxUsage
        $hostCpuUsage = $cpuStats.HostUsage
        
        # --- 决策依据：取两者中的最大值 ---
        $effectiveUsage = [math]::Max($vcpuMaxUsage, $hostCpuUsage)
        
        # --- 核心切换逻辑 (与v12.4相同) ---
        if ($isSwitching) {
            # ... (场景0: 等待切换)
            $statusMessage = "状态: 正在切换到 '$($targetPlan)'，等待系统确认..."
            if ($currentPlan -eq $targetPlan) {
                $statusMessage = "状态: 已确认切换到 '$($targetPlan)'，恢复监控。"
                $isSwitching = $false
                $targetPlan = $null
            }
        }
        elseif ($effectiveUsage -gt $HighLoadThreshold) {
            # ... (场景1: 高负载)
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
            # ... (场景2: 非高负载)
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
        
        # --- 状态显示 ---
        $logLine = @(
            "$(Get-Date -Format 'HH:mm:ss')", "| 模式: $($currentPlan.PadRight(10))",
            "| vCPU: $('{0,6:F2}' -f $vcpuMaxUsage) %", "| 宿主机: $('{0,6:F2}' -f $hostCpuUsage) %",
            "| vCPU源: $($cpuStats.VcpuSource.PadRight(28))", "| $($statusMessage)"
        ) -join ' '
        Write-Host $logLine
        
        Start-Sleep -Seconds $PollingIntervalS
    }
}
finally {
    # 优雅退出
    Write-Host "`n"; Write-Host "脚本正在停止... 正在恢复到“节能”模式..." -ForegroundColor Yellow
    $finalPlan = Get-CurrentHostPowerPlan
    Set-HostPowerPlan -TargetPlanName $PowerSaverPlanName -CurrentPlanName $finalPlan
    Write-Host "已恢复到“节能”模式。程序退出。" -ForegroundColor Green
}