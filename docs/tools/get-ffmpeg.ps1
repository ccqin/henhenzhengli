# 获取 ffmpeg（gyan.dev essentials GPL 构建，含 libx265 软编 + hevc_qsv 硬编）
# 用途：视频壁纸 HEVC@30 预处理转码（GPU 第三梯队·方案 2）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File docs/tools/get-ffmpeg.ps1
#     → 下载+解压到 artifacts\ffmpeg\（构建缓存，MSIX 打包用）
#   powershell -ExecutionPolicy Bypass -File docs/tools/get-ffmpeg.ps1 -InstallUserTools
#     → 另复制到 %APPDATA%\DesktopManager\tools\（Debug 直跑调试用；
#       WallpaperTranscoder 工具查找顺序：应用目录 → 用户 tools 目录）
param(
    [string]$OutDir = "",
    [switch]$InstallUserTools
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # 仓库根
if (-not $OutDir) { $OutDir = Join-Path $root "artifacts\ffmpeg" }

# 固定源（可重复构建）；BtbN GPL 构建（含 libx265 软编），只取 ffmpeg.exe——
# 视频探测用 `ffmpeg -i` 的 stderr 解析，无需 ffprobe（省一半工具体积）。
# 注：gyan.dev 国内 SSL 常被重置（真机教训），故用 GitHub BtbN 源
$name = "ffmpeg-master-latest-win64-gpl"
$zip  = Join-Path $OutDir "$name.zip"
$ff   = Join-Path $OutDir "ffmpeg.exe"

if (Test-Path $ff) {
    Write-Host "已就绪：$OutDir"
}
else {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$name.zip"
    Write-Host "下载 $url（~100MB，断点续传，中断自动重试）"
    $ok = $false
    foreach ($i in 1..60) {
        & curl.exe -sS -L -C - -o $zip $url
        if ($LASTEXITCODE -eq 0) { $ok = $true; break }
        Write-Host "  中断（exit $LASTEXITCODE），重试 $i..."
        Start-Sleep -Seconds 1
    }
    if (-not $ok) { throw "下载失败：$url" }

    Write-Host "解压（只取 ffmpeg.exe）..."
    Expand-Archive $zip (Join-Path $OutDir "tmp") -Force
    Copy-Item (Join-Path $OutDir "tmp\$name\bin\ffmpeg.exe") $ff -Force
    Remove-Item (Join-Path $OutDir "tmp") -Recurse -Force
    Remove-Item $zip -Force
    Write-Host "完成：$OutDir"
}

if ($InstallUserTools) {
    $t = Join-Path $env:APPDATA "DesktopManager\tools"
    New-Item -ItemType Directory -Force -Path $t | Out-Null
    Copy-Item $ff $t -Force
    Write-Host "已安装到用户工具目录：$t（Debug 直跑时 WallpaperTranscoder 从这里找）"
}
