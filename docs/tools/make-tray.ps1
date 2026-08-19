# 生成托盘图标（tray.ico）— 手写标准 32bpp BMP 条目 ICO，颜色无损
#
# 用法：改下面两个路径后运行  powershell -ExecutionPolicy Bypass -File docs/tools/make-tray.ps1
#   $pngPath  : 源图（建议正方形、带透明，任意尺寸，高质量缩放到 32px）
#   $icoPath  : 输出（默认项目 Assets/tray.ico，与 App.xaml 的 IconSource 对应）
#
# 背景（为什么这么麻烦）：
#   - H.NotifyIcon 的 IconSource 不认 PNG（"picture must be Icon"，32px 也不行）
#   - System.Drawing Icon.Save 生成的 ICO 有损/丢色（颜色不对的元凶）
#   - PNG 压缩条目的 ico（Vista+ 新格式）WPF 不认
#   → 唯一稳路：手工组装标准 BMP 条目 ICO（LockBits 直拷 BGRA，零转换）

param(
    [string]$pngPath = "D:\15.ai\狠狠整理\docs\desktop-tool-512_5bf592be.png",
    [string]$icoPath = "D:\15.ai\狠狠整理\src\DesktopManager.App\Assets\tray.ico",
    [int]$sz = 32
)

Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile($pngPath)
$bmp = New-Object System.Drawing.Bitmap $sz, $sz
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.DrawImage($src, 0, 0, $sz, $sz)
$g.Dispose(); $src.Dispose()

$rect = New-Object System.Drawing.Rectangle 0, 0, $sz, $sz
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$xor = New-Object byte[] ($sz * $sz * 4)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $xor, 0, $xor.Length)
$bmp.UnlockBits($data); $bmp.Dispose()

# DIB 行序：自下而上
$stride = $sz * 4
$flipped = New-Object byte[] $xor.Length
for ($y = 0; $y -lt $sz; $y++) {
    [Array]::Copy($xor, $y * $stride, $flipped, ($sz - 1 - $y) * $stride, $stride)
}

$and = New-Object byte[] ($sz * 4)          # AND 掩码全 0（alpha 通道生效）
$xorSize = $flipped.Length
$imageSize = 40 + $xorSize + ($sz * 4)

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
# ICONDIR（6B）+ ICONDIRENTRY（16B）
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]1)
$bw.Write([Byte]$sz); $bw.Write([Byte]$sz); $bw.Write([Byte]0); $bw.Write([Byte]0)
$bw.Write([UInt16]1); $bw.Write([UInt16]32)
$bw.Write([UInt32]$imageSize); $bw.Write([UInt32]22)     # 数据起点 offset=6+16
# BITMAPINFOHEADER（40B，高度=2 倍：XOR+AND）
$bw.Write([UInt32]40); $bw.Write([Int32]$sz); $bw.Write([Int32]($sz*2))
$bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
$bw.Write([UInt32]$xorSize); $bw.Write([Int32]0); $bw.Write([Int32]0)
$bw.Write([UInt32]0); $bw.Write([UInt32]0)
$bw.Write($flipped)
$bw.Write($and)
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
Write-Host "OK: $icoPath（$imageSize 字节，${sz}x${sz}）"
