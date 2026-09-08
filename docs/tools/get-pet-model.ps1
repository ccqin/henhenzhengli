# 下载 Live2D 官方示例模型到宠物插件 assets 目录
# 用法：powershell -ExecutionPolicy Bypass -File docs/tools/get-pet-model.ps1
# 模型：Live2D 官方 Cubism 样例（Hiyori）——免费素材许可，应用内展示允许（遵守署名条款）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$petAssets = Join-Path $root "src\DesktopManager.Plugin.Pet\bin\Debug\net10.0-windows10.0.19041.0\assets"
New-Item -ItemType Directory -Force -Path $petAssets | Out-Null

$url = "https://cdn.jsdelivr.net/gh/dylanNew/live2d-widget-models@master/packages/live2d-widget-model-shizuku/assets/shizuku.model.json"
Write-Host "TODO: 官方 Cubism3 (model3.json) 示例需从 Live2D 官方下载包获取"
Write-Host "临时方案：手动放置 *.model3.json 模型目录到 $petAssets\<角色>\"
Write-Host "下载官方示例包: https://www.live2d.com/en/learn/sample/"
