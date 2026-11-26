#Requires -Version 5.1
param([string]$Directory)

# Windows PowerShell supplies synchronous standard streams to the real Server
# child. A private file signals when the HTTP client has finished its session.
$ErrorActionPreference = 'Stop'
$request = $env:EXHYPERV_TEST_COMMAND | ConvertFrom-Json
Remove-Item Env:EXHYPERV_TEST_COMMAND
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $request.executable
# Quote for Windows argv, doubling backslashes before literal and closing quotes.
$start.Arguments = (@(foreach ($arg in $request.arguments) {
    $escaped = $arg -replace '(\\*)"', '$1$1\"'
    '"' + ($escaped -replace '(\\+)$', '$1$1') + '"'
}) -join ' ')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$child = [Diagnostics.Process]::Start($start)
try {
    $output = $child.StandardOutput.ReadToEndAsync()
    $errors = $child.StandardError.ReadToEndAsync()
    if ($request.interactive) {
        $shutdown = Join-Path $Directory 'shutdown'
        while (!$child.HasExited -and !(Test-Path -LiteralPath $shutdown)) { Start-Sleep -Milliseconds 25 }
        if (!$child.HasExited) { $child.StandardInput.WriteLine('shutdown') }
    } elseif ($null -ne $request.input) {
        $child.StandardInput.WriteLine($request.input)
    }
    $child.StandardInput.Close()
    $child.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $Directory 'stdout.txt'), $output.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $Directory 'stderr.txt'), $errors.GetAwaiter().GetResult())
    exit $child.ExitCode
} finally {
    if (!$child.HasExited) { $child.Kill() }
    $child.Dispose()
}
