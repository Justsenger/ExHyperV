# Each checkpoint records the expected visible state and one original PNG.
Record-Scene 'vm-list' '虚拟机页面显示搜索框和列表' {
    Select-Page 'NavVirtualMachines' 'VmList'
    $null = Get-Control 'SearchBox'
}
Record-Scene 'vm-search-empty' '输入无匹配名称后列表为空' {
    $search = (Get-Control 'SearchBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $query = [guid]::NewGuid().ToString()
    $search.SetValue($query)
    $list = Get-Control 'VmList'
    $item = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    $null = Wait-Until { $search.Current.Value -eq $query -and !$list.FindFirst('Children', $item) } 'empty filtered list'
}
Record-Scene 'vm-search-cleared' '清除筛选后回到虚拟机列表' {
    $search = (Get-Control 'SearchBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $search.SetValue('')
    $null = Wait-Until { $search.Current.Value -eq '' } 'empty search text'
}
Record-Scene 'host-overview' '宿主页显示本机名称及概览' {
    Select-Page 'NavHost' 'HostNameText'
    $null = Wait-Until { (Get-Control 'HostNameText').Current.Name -eq $env:COMPUTERNAME } 'host name'
}
Record-Scene 'web-page' 'Web 页面显示当前标题' {
    Select-Page 'NavWeb' 'TitleHeader'
}
Record-Scene 'settings-appearance' '应用设置显示语言与外观选项' {
    Select-Page 'NavSettings' 'ThemePicker'
}
Record-Scene 'settings-color-picker' '自定义选色浮层显示取消和应用按钮' {
    $button = Get-Control 'CustomColorButton'
    $null = [AppTestWindow]::SetForegroundWindow($handle)
    $button.SetFocus()
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Get-Control 'CancelColorButton'
}
Record-Scene 'settings-color-cancelled' '取消后选色浮层关闭，返回设置页' {
    (Get-Control 'CancelColorButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-Until { !(Find-Control 'CancelColorButton') } 'closed color picker'
}

$original = [AppTestWindow+Rect]::new()
if (![AppTestWindow]::GetWindowRect($handle, [ref]$original)) { throw 'Cannot read original window size.' }
$originalWidth = $original.Right - $original.Left
$height = $original.Bottom - $original.Top
$targetWidth = [int](780 * [AppTestWindow]::GetDpiForWindow($handle) / 96)
try {
    Record-Scene 'settings-narrow' '约 780 DIP 窄窗口下的应用设置和紧凑导航' {
        if (![AppTestWindow]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0, $targetWidth, $height, 22)) { throw 'Cannot resize test window.' }
        $null = Wait-Until {
            $actual = [AppTestWindow+Rect]::new()
            [AppTestWindow]::GetWindowRect($handle, [ref]$actual) -and ($actual.Right - $actual.Left -eq $targetWidth)
        } 'narrow window size'
        $null = Get-Control 'ThemePicker'
    }
} finally {
    $null = [AppTestWindow]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0, $originalWidth, $height, 22)
}
