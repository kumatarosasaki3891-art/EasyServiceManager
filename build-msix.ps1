param(
    [ValidateSet("Store", "Sideload")]
    [string]$Mode = "Store",

    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",

    [string]$Configuration = "Release",
    [string]$Version = "1.1.0.0"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProj = Join-Path $scriptDir "EasyServiceManager\EasyServiceManager.fsproj"
$pkgDir = Join-Path $scriptDir "EasyServiceManager.Package"
$manifestTemplate = Join-Path $pkgDir "Package.appxmanifest"
$imagesDir = Join-Path $pkgDir "Images"

Write-Host "=== EasyServiceManager (簡単サービス診断) MSIX Build Tool ===" -ForegroundColor Cyan
Write-Host "Mode: $Mode | Arch: $Architecture | Configuration: $Configuration | Version: $Version" -ForegroundColor Yellow

# Windows SDK ツール (makeappx.exe, makepri.exe, signtool.exe) の検索
$kitBinRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$sdkArchDir = Get-ChildItem -Path $kitBinRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName "x64\makeappx.exe") } |
    Sort-Object Name -Descending |
    Select-Object -First 1

if (-not $sdkArchDir) {
    throw "Windows 10/11 SDK (makeappx.exe) が見つかりませんでした。Windows SDK をインストールしてください。"
}

$sdkToolsDir = Join-Path $sdkArchDir.FullName "x64"
$makeappx = Join-Path $sdkToolsDir "makeappx.exe"
$makepri = Join-Path $sdkToolsDir "makepri.exe"
$signtool = Join-Path $sdkToolsDir "signtool.exe"
Write-Host "Using Windows SDK tools: $sdkToolsDir" -ForegroundColor DarkGray

$rid = "win-$Architecture"
$workRoot = Join-Path $pkgDir "obj\msix\$Configuration\$rid"
$publishDir = Join-Path $workRoot "publish"
$stageDir = Join-Path $workRoot "stage"

if (Test-Path $workRoot) {
    Remove-Item -Path $workRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir, $stageDir | Out-Null

# 1. Self-contained ビルドを発行
Write-Host "Publishing EasyServiceManager ($rid)..." -ForegroundColor Green
dotnet publish $appProj `
    -c $Configuration `
    -r $rid `
    --self-contained true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# 2. MSIX ステージングディレクトリを構成
Copy-Item -Path (Join-Path $publishDir "*") -Destination $stageDir -Recurse -Force
$stageImagesDir = Join-Path $stageDir "Images"
New-Item -ItemType Directory -Force -Path $stageImagesDir | Out-Null
Copy-Item -Path (Join-Path $imagesDir "*") -Destination $stageImagesDir -Force

# 3. AppxManifest.xml をステージング用に具体化
$manifestContent = Get-Content -Path $manifestTemplate -Raw -Encoding UTF8
$manifestContent = $manifestContent.Replace('$targetnametoken$.exe', 'EasyServiceManager.exe')
$manifestContent = $manifestContent.Replace('$targetentrypoint$', 'Windows.FullTrustApplication')
$manifestContent = $manifestContent.Replace('<Resource Language="x-generate"/>', '<Resource Language="ja-JP"/>')
[xml]$manifestXml = $manifestContent
$identityNode = $manifestXml.Package.Identity
$identityNode.SetAttribute("Version", $Version)
$identityNode.SetAttribute("ProcessorArchitecture", $Architecture)
$stageManifest = Join-Path $stageDir "AppxManifest.xml"
$xmlWriterSettings = [System.Xml.XmlWriterSettings]::new()
$xmlWriterSettings.Encoding = [System.Text.UTF8Encoding]::new($false)
$xmlWriterSettings.Indent = $true
$xmlWriter = [System.Xml.XmlWriter]::Create($stageManifest, $xmlWriterSettings)
$manifestXml.Save($xmlWriter)
$xmlWriter.Close()

# 4. resources.pri を生成（単一 MSIX 向けに分割なし構成でタイル/スケール画像をインデックス）
$priConfig = Join-Path $workRoot "priconfig.xml"
$priOutput = Join-Path $stageDir "resources.pri"
& $makepri createconfig /cf $priConfig /dq ja-JP /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed." }
[xml]$priXml = Get-Content -Path $priConfig -Raw -Encoding UTF8
if ($priXml.resources.packaging) {
    $priXml.resources.RemoveChild($priXml.resources.packaging) | Out-Null
}
$priXml.resources.index.SetAttribute("startIndexAt", "Images")
$priXml.Save($priConfig)
& $makepri new /pr $stageDir /cf $priConfig /mn $stageManifest /of $priOutput /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri new failed." }

# 5. MSIX パッケージをパック
$outDir = if ($Mode -eq "Store") {
    Join-Path $pkgDir "AppPackages\Upload"
} else {
    Join-Path $pkgDir "AppPackages\Sideload"
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$msixPath = Join-Path $outDir "EasyServiceManager_${Version}_${Architecture}.msix"

Write-Host "Packing MSIX..." -ForegroundColor Green
& $makeappx pack /d $stageDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) {
    throw "makeappx pack failed with exit code $LASTEXITCODE"
}

if ($Mode -eq "Sideload") {
    $pfxPath = Join-Path $pkgDir "EasyServiceManager_TemporaryKey.pfx"
    $cerPath = Join-Path $outDir "EasyServiceManager.cer"
    $publisherSubject = $identityNode.GetAttribute("Publisher")
    if (-not $publisherSubject) {
        $publisherSubject = "CN=XXXX"
    }
    if (-not (Test-Path $pfxPath)) {
        Write-Host "Generating self-signed certificate for $publisherSubject..." -ForegroundColor Yellow
        $cert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $publisherSubject `
            -KeyUsage DigitalSignature `
            -FriendlyName "EasyServiceManager Sideload Test" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        $emptyPwd = ConvertTo-SecureString -String "" -Force -AsPlainText
        Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $emptyPwd | Out-Null
        Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
    } else {
        $pfxCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, "")
        [System.IO.File]::WriteAllBytes($cerPath, $pfxCert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }

    Write-Host "Signing Sideload MSIX..." -ForegroundColor Green
    & $signtool sign /fd SHA256 /f $pfxPath $msixPath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool sign failed with exit code $LASTEXITCODE"
    }
    Write-Host "`nBuild complete!" -ForegroundColor Green
    Write-Host "Sideload MSIX: $msixPath" -ForegroundColor Cyan
    Write-Host "Public Cert (.cer): $cerPath" -ForegroundColor Cyan
} else {
    Write-Host "`nBuild complete!" -ForegroundColor Green
    Write-Host "Microsoft Store MSIX: $msixPath" -ForegroundColor Cyan
    Write-Host "Microsoft Partner Center で上記 MSIX パッケージをアップロードしてください。" -ForegroundColor Yellow
}
