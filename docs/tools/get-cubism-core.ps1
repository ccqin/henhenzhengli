# 获取 Live2DCubismCore.dll（Cubism Native SDK 核心）
# 此 DLL 是 Live2D 官方闭源免费分发（个人/小企业免费许可）
#
# 手动步骤（一次性）：
# 1. 打开 https://www.live2d.com/en/sdk/about/native/
# 2. 点击 "Download Cubism SDK for Native"（需同意许可协议，免费）
# 3. 下载 CubismSdkForNative-5-r1.zip
# 4. 解压后找到 dll\x86_64\windows\Live2DCubismCore.dll
# 5. 复制到 src\DesktopManager.Plugin.Pet\bin\Debug\net10.0-windows10.0.19041.0\
#    以及 %APPDATA%\DesktopManager\plugins\com.desktopmanager.pet\
#
# VPet 项目也内嵌了此 DLL（Apache-2.0 项目但 Core 本身是 Live2D 许可）：
# https://github.com/LorisYounger/VPet.Live2DAnimation 的 Release 包含
Write-Host "请按上述注释手动下载 Live2DCubismCore.dll"
