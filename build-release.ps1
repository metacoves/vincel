# 发版脚本：准备内容 -> Inno Setup生成安装包 -> 绿色版zip -> 计算SHA256 -> 生成update.json
param(
 [string]$Notes = "",
 [bool]$Force = $false,
 [string]$Domain = "https://vincel.netlify.app"
)

$ErrorActionPreference = "Stop"
$proj = $PSScriptRoot
$buildDir = if (Test-Path "$proj\bin\Release\Vincel.exe") { "$proj\bin\Release" } else { "$proj\bin\Debug" }
$out = "$proj\发布包"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

# 0. 版本号与产物名
$exe = Get-Item "$buildDir\Vincel.exe"
$version = $exe.VersionInfo.ProductVersion
$installerName = "Vincel_v${version}_Setup.exe"
$zipName = "Vincel_v${version}_portable.zip"

# 1. 准备安装内容（排除pdb和arm64运行库）
$pkg = "$out\_pkg"
New-Item -ItemType Directory -Path $pkg | Out-Null
Get-ChildItem $buildDir -Exclude *.pdb | Copy-Item -Destination $pkg -Recurse -Force
$rt = "$pkg\runtimes\win-arm64"
if (Test-Path $rt) { Remove-Item $rt -Recurse -Force }

# 1.5 绿色版附带：WebView2运行时引导 + 使用说明（安装器会排除这两个文件）
$wvBoot = "$proj\WebView2Runtime\MicrosoftEdgeWebview2Setup.exe"
if (Test-Path $wvBoot) {
 Copy-Item $wvBoot "$pkg\MicrosoftEdgeWebview2Setup.exe" -Force
 $readme = "深澈 Vincel 绿色版（免安装）`r`n`r`n使用方法：`r`n1. 将本文件夹解压到任意目录`r`n2. 双击 Vincel.exe 即可运行，无需安装`r`n`r`n提示：`r`n- 如提示缺少 WebView2 运行时（常见于 Win7 / 部分 Win10），请先双击 MicrosoftEdgeWebview2Setup.exe 安装，再运行软件`r`n- Windows 11 自带 WebView2，可直接运行`r`n`r`n卸载：直接删除本文件夹即可，无残留。"
 Set-Content -Path "$pkg\使用说明.txt" -Value $readme -Encoding UTF8
} else {
 Write-Host "警告：未找到 WebView2 引导包，绿色版将不含运行时引导" -ForegroundColor Yellow
}

# 2. 用 Inno Setup 生成安装包（自动探测安装位置：默认目录或注册表）
$iscc = Get-ChildItem "C:\Program Files (x86)\Inno Setup 6\ISCC.exe","D:\Inno Setup 6\ISCC.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
if (!$iscc) {
  $loc = Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
         Where-Object { $_.DisplayName -like "Inno Setup*" } | Select-Object -First 1 -ExpandProperty InstallLocation
  if ($loc) { $iscc = Join-Path $loc "ISCC.exe" }
}
if (!(Test-Path $iscc)) {
 Write-Host "未找到 Inno Setup，请先安装（免费）：https://jrsoftware.org/isdl.php"
 Write-Host "安装后重新运行本脚本。"
 exit 1
}
& $iscc "$proj\VincelSetup.iss" /DAppVersion=$version | Out-Null

# 3. 绿色版zip（在删除_pkg之前打包）
Compress-Archive -Path "$pkg\*" -DestinationPath "$out\$zipName" -CompressionLevel Optimal

# 4. SHA256 + update.json
$installer = "$out\$installerName"
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLower()
$update = @{
 version = $version
 url = "$Domain/$installerName"
 sha256 = $hash
 notes = $Notes
 force = $Force
} | ConvertTo-Json
$update | Set-Content -Path "$out\update.json" -Encoding UTF8
Remove-Item $pkg -Recurse -Force

# 5. 同步到官网站点
$site = "$proj\官网站点"
if (Test-Path $site) {
 Copy-Item "$out\$installerName" $site -Force
 Copy-Item "$out\$zipName" $site -Force
 Copy-Item "$out\update.json" $site -Force
}

Write-Host ""
Write-Host "打包完成：$out"
Write-Host "安装包：$installerName ($version, SHA256: $hash)"
Write-Host "绿色版：$zipName"
Write-Host "下一步：把 index.html、$installerName、$zipName、update.json 上传到 GitHub 仓库根目录。"
