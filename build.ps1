# 编译 Dustline 单机辅助插件
#
# 用法：
#   .\build.ps1                      # 默认游戏目录 D:\cs
#   .\build.ps1 -GameDir "E:\Games\Dustline"
#
# 需要 .NET Framework 的 csc.exe（Windows 自带），不需要 Visual Studio。

param(
    [string]$GameDir = "D:\cs"
)

# 找 csc.exe（64 位框架；32 位系统回退到 Framework）
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
    Write-Host "找不到 csc.exe（需要 .NET Framework 4.0+）" -ForegroundColor Red
    exit 1
}

# 检查游戏目录
if (-not (Test-Path $GameDir)) {
    Write-Host "游戏目录不存在: $GameDir" -ForegroundColor Red
    exit 1
}
$pluginDir = Join-Path $GameDir "BepInEx\plugins"
if (-not (Test-Path $pluginDir)) {
    Write-Host "没找到 $pluginDir —— 先装 BepInEx 5.4.x（x64 Mono 版）" -ForegroundColor Red
    exit 1
}

# 游戏在跑的话 DLL 会被占用，编译会失败，所以先关掉
if (Get-Process Dustline -ErrorAction SilentlyContinue) {
    Write-Host "检测到游戏还在运行，先关掉它（否则 DLL 被占用编译会失败）" -ForegroundColor Yellow
    Get-Process Dustline | Stop-Process -Force
    Start-Sleep -Seconds 3
}

# 检查引用程序集
$refs = @(
    (Join-Path $GameDir "BepInEx\core\BepInEx.dll"),
    (Join-Path $GameDir "BepInEx\core\0Harmony.dll"),
    (Join-Path $GameDir "Dustline_Data\Managed\UnityEngine.dll"),
    (Join-Path $GameDir "Dustline_Data\Managed\UnityEngine.CoreModule.dll"),
    (Join-Path $GameDir "Dustline_Data\Managed\UnityEngine.IMGUIModule.dll"),
    (Join-Path $GameDir "Dustline_Data\Managed\UnityEngine.InputLegacyModule.dll"),
    (Join-Path $GameDir "Dustline_Data\Managed\netstandard.dll")
)
foreach ($r in $refs) {
    if (-not (Test-Path $r)) {
        Write-Host "缺少引用程序集: $r" -ForegroundColor Red
        exit 1
    }
}

$out = Join-Path $pluginDir "DustlineAssist.dll"
Remove-Item $out -Force -ErrorAction SilentlyContinue

Write-Host "编译 Assist.cs ..." -ForegroundColor Cyan
& $csc /target:library /out:$out `
    /reference:"$($refs[0])" `
    /reference:"$($refs[1])" `
    /reference:"$($refs[2])" `
    /reference:"$($refs[3])" `
    /reference:"$($refs[4])" `
    /reference:"$($refs[5])" `
    /reference:"$($refs[6])" `
    /nologo "$PSScriptRoot\Assist.cs"

if (Test-Path $out) {
    $kb = [math]::Round((Get-Item $out).Length / 1KB, 1)
    Write-Host "编译成功 -> $out  ($kb KB)" -ForegroundColor Green
    Write-Host "启动游戏后按 Insert 打开设置窗口。" -ForegroundColor Green
} else {
    Write-Host "编译失败" -ForegroundColor Red
    exit 1
}
