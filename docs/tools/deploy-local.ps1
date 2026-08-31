# 本地侧加载部署一条龙：打包 → 签名 → 杀进程 → 卸旧 → 安装 → 启动 → 验证
# 用法：powershell -ExecutionPolicy Bypass -File docs/tools/deploy-local.ps1 [-Version 1.0.3.0]
#
# 前置（一次性）：
#   证书 DesktopManager-store.cer（Subject=商店 Publisher CN=00588731-...）已导入
#   Cert:\LocalMachine\TrustedPeople（管理员）。签名指纹写死在下方，换证书时更新。
# 真机教训（2026-08-31 连环乌龙）：
#   - 忘签名 / 用旧证书签（清单 Publisher 换商店身份后不匹配 → SignerSign 0x8007000b）
#   - 应用运行中卸载/安装静默失败（须先杀干净）
#   - 部署后不验证版本/进程 → "以为装了其实没装"
param([string]$Version = "1.0.3.0")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifacts = Join-Path $root "artifacts"
$certThumbprint = "D6615751932F3543291779301B39E7D9AF72BFCC"   # Subject=CN=00588731-...（商店 Publisher 同身份）

function Step($msg) { Write-Host ("==> " + $msg) -ForegroundColor Cyan }

# 1) 打包（build-msix.ps1 内含 publish/清理/完整性抽查）
Step "打包 $Version"
& (Join-Path $PSScriptRoot "build-msix.ps1") -Version $Version
$msix = Join-Path $artifacts "DesktopManager_${Version}_x64.msix"

# 2) 签名（清单 Publisher=CN=00588731-...，必须同 Subject 证书；Start-Process 方式取真实退出码）
Step "签名"
$signtool = (Get-Item (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\*\x64\signtool.exe") | Sort-Object FullName -Descending | Select-Object -First 1).FullName
$p = Start-Process $signtool -ArgumentList @('sign','/fd','SHA256','/sha1',$certThumbprint,$msix) -Wait -PassThru -NoNewWindow
if ($p.ExitCode -ne 0) { throw "signtool 失败 exit=$($p.ExitCode)（证书在 CurrentUser\My？指纹过期？）" }

# 3) 杀干净（运行中会阻止 MSIX 卸载/升级——静默失败的根源）
Step "停止现有实例"
Get-Process DesktopManager* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2

# 4) 卸旧 + 装
Step "卸载旧版并安装"
Get-AppxPackage *qins* -ErrorAction SilentlyContinue | Remove-AppxPackage
Add-AppxPackage -Path $msix

# 5) 启动 + 验证（版本号 + 进程数 + 运行路径——防"以为装了"）
Step "启动并验证"
$pkg = Get-AppxPackage *qins*
if ($pkg.Version -ne $Version) { throw "安装版本不符：期望 $Version 实际 $($pkg.Version)" }
$aumid = (Get-AppxPackageManifest $pkg).Package.Applications.Application.Id | Select-Object -First 1
Start-Process ("shell:AppsFolder\" + $pkg.PackageFamilyName + "!" + $aumid)
Start-Sleep 12
$procs = Get-Process DesktopManager* -ErrorAction SilentlyContinue
if ($procs.Count -lt 3) { throw "进程数异常：$($procs.Count)（主进程+子进程应≥3）" }
Write-Host ("部署完成：v" + $pkg.Version + "  进程 " + $procs.Count + " 个  " + $procs[0].Path) -ForegroundColor Green
