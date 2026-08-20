# MSIX 打包脚本（本地侧加载测试用；商店提交时用 Partner Center 的打包上传流程）
#
# 用法：powershell -ExecutionPolicy Bypass -File docs/tools/build-msix.ps1
# 产物：artifacts\DesktopManager_1.0.0.0_x64.msix（自签名，需信任证书后侧加载安装）
#
# 前置（本机一次性）：
#   1. 以管理员运行：New-SelfSignedCertificate -Type Custom -Subject "CN=DesktopManager Dev"
#        -KeyUsage DigitalSignature -FriendlyName "DesktopManager Dev" `
#        -CertStoreLocation "Cert:\LocalMachine\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
#   2. 导出并安装到「受信任人」：见脚本末尾注释
param(
    [string]$CertThumbprint = "",   # 自签名证书指纹（空则跳过签名）
    [string]$Version = "1.0.0.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # 仓库根
$artifacts = Join-Path $root "artifacts"
$layout = Join-Path $artifacts "msix-layout"

# 1) Release 发布（self-contained false，依赖框架；三 exe 都会进输出）
Write-Host "==> dotnet publish (Release)"
dotnet publish (Join-Path $root "src\DesktopManager.App\DesktopManager.App.csproj") `
    -c Release -o $layout --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish 失败" }

# 2) manifest + 图标进包根
Copy-Item (Join-Path $root "src\DesktopManager.App\Package.appxmanifest") (Join-Path $layout "AppxManifest.xml") -Force
New-Item -ItemType Directory -Force -Path (Join-Path $layout "Assets") | Out-Null
Copy-Item (Join-Path $root "src\DesktopManager.App\Assets\icon44.png") $layout\Assets -Force
Copy-Item (Join-Path $root "src\DesktopManager.App\Assets\icon150.png") $layout\Assets -Force
Copy-Item (Join-Path $root "src\DesktopManager.App\Assets\store-logo.png") $layout\Assets -Force
# MSIX 内 manifest 的 Identity 版本同步
$manifest = Get-Content (Join-Path $layout "AppxManifest.xml") -Raw -Encoding UTF8
$manifest = $manifest -replace 'Version="1\.0\.0\.0"', "Version=""$Version"""
Set-Content (Join-Path $layout "AppxManifest.xml") $manifest -Encoding UTF8

# 3) 清理不需要进包的产物（pdb 可留可去——留便于诊断）
Get-ChildItem $layout -Filter "*.pdb" | Remove-Item

# 4) makeappx
$makeappx = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\*\x64\makeappx.exe"
$makeappx = (Get-Item $makeappx | Sort-Object FullName -Descending | Select-Object -First 1).FullName
if (-not $makeappx) { throw "找不到 makeappx（需 Windows SDK）" }
$msix = Join-Path $artifacts "DesktopManager_${Version}_x64.msix"
Write-Host "==> makeappx"
& $makeappx pack /d $layout /p $msix /nv
if ($LASTEXITCODE -ne 0) { throw "makeappx 失败" }

# 5) 签名（可选）
if ($CertThumbprint -ne "") {
    $signtool = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\*\x64\signtool.exe"
    $signtool = (Get-Item $signtool | Sort-Object FullName -Descending | Select-Object -First 1).FullName
    Write-Host "==> signtool"
    & $signtool sign /fd SHA256 /a /f $null /sha1 $CertThumbprint $msix
    if ($LASTEXITCODE -ne 0) { throw "签名失败" }
} else {
    Write-Host "跳过签名（-CertThumbprint 传入指纹以签名）"
}

Write-Host "`n完成：$msix"
Write-Host @"

安装（侧加载）：
  1. 管理员导出证书并装入「受信任人」：
     $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object FriendlyName -eq 'DesktopManager Dev'
     $pwd = ConvertTo-SecureString -String '1234' -Force -AsPlainText
     Export-Certificate -Cert $cert -FilePath artifacts\DesktopManager.cer
     Import-Certificate -FilePath artifacts\DesktopManager.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
  2. Add-AppxPackage -Path <包路径>
  卸载：Get-AppxPackage *DesktopManager* | Remove-AppxPackage
"@
