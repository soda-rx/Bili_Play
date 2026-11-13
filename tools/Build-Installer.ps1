# Build a clean production MSIX. Signing keys stay outside the repository and are never distributed.
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& python -B (Join-Path $PSScriptRoot 'check_release.py')
if ($LASTEXITCODE -ne 0) { throw '版本预检未通过。' }
[xml]$releaseProject = Get-Content -LiteralPath (Join-Path $root 'BiliPlay.csproj') -Raw
if ($releaseProject.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Contains('-')) { throw '预发布安装链路尚未配置，请先规划预览渠道及升级版本，不能当作正式版打包。' }
[xml]$sourceManifest = Get-Content -LiteralPath (Join-Path $root 'Package.appxmanifest') -Raw
$version = ([Version]$sourceManifest.Package.Identity.Version).ToString(3)
$publisher = [string]$sourceManifest.Package.Identity.Publisher
& (Join-Path $PSScriptRoot 'Test-StoreIdentity.ps1') -Manifest $sourceManifest
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([Environment]::GetFolderPath('Desktop')) ('Bili_Play_' + $version) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output -eq $root -or $output.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '安装包输出必须在工作区之外。' }
if (Test-Path -LiteralPath $output) {
    $allowed = @(('BiliPlay_' + $version + '_x64.msix'), 'BiliPlay.cer')
    if (@(Get-ChildItem -LiteralPath $output -Force | Where-Object { $_.PSIsContainer -or $_.Name -notin $allowed -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) }).Count) { throw '交付目录包含其他文件或链接，请选择空目录。' }
}
if (Test-Path -LiteralPath (Join-Path $output ('BiliPlay_' + $version + '_x64.msix'))) {
    $existing = Get-Item -LiteralPath (Join-Path $output ('BiliPlay_' + $version + '_x64.msix')) -Force
    if ($existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '安装包目标位置是目录或链接。' }
}
# Shared-runtime and self-contained builds have different PRI inputs. Never reuse their intermediate XAML/PRI cache.
& (Join-Path $PSScriptRoot 'Clean-Generated.ps1') -IncludePublishOutput
$stage = Join-Path $root 'artifacts/installer'
$signing = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('UserProfile')) ('.biliplay/signing/' + $sourceManifest.Package.Identity.Name)))
if ($signing.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '签名私钥不能保存在工作区。' }
foreach ($directory in @($output, $signing, $stage)) {
    $ancestor = $directory
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '构建路径不能包含链接。' }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
$pfx = Join-Path $signing 'BiliPlay.pfx'
$secretFile = Join-Path $signing 'password.dpapi'
$publicCert = Join-Path $signing 'BiliPlay.cer'
$package = Join-Path $stage ('BiliPlay_' + $version + '_x64.msix')
$password = $null
Push-Location $root
try {
    if ((Test-Path -LiteralPath $pfx) -ne (Test-Path -LiteralPath $secretFile)) { throw '签名密钥文件不完整，停止打包。' }
    if (-not (Test-Path -LiteralPath $pfx)) {
        $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(36))
        & winapp cert generate --publisher $publisher --output $pfx --password $password --valid-days 365 --export-cer --quiet *> $null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $pfx)) { throw '生成项目专用证书失败。' }
        ConvertTo-SecureString $password -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -LiteralPath $secretFile
    } else {
        $secret = (Get-Content -LiteralPath $secretFile -Raw).Trim() | ConvertTo-SecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
        try { $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer); $secret.Dispose() }
    }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfx, $password)
    try {
        if ($certificate.Subject -ne $publisher -or -not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw '证书发布者、私钥或有效期不符合要求。' }
        [IO.File]::WriteAllBytes($publicCert, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    } finally { $certificate.Dispose() }
    & winapp package (Join-Path $root 'BiliPlay.csproj') -c Release --arch x64 --self-contained --no-sign --output $package -p Platform=x64 -p DevelopmentBuild=false -p EnableVisualChecks=false -p SelfContained=true -p DebugType=None -p DebugSymbols=false -p ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw '正式安装包构建失败。' }
    & winapp sign $package $pfx --password $password --quiet *> $null
    if ($LASTEXITCODE -ne 0) { throw '安装包签名失败。' }
    $password = $null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        function Read-Entry([string]$name) {
            $entry = $zip.GetEntry($name)
            if (-not $entry) { throw ('安装包缺少文件：' + $name) }
            $stream = $entry.Open(); $memory = [IO.MemoryStream]::new()
            try { $stream.CopyTo($memory); return ,$memory.ToArray() }
            finally { $stream.Dispose(); $memory.Dispose() }
        }
        $names = @($zip.Entries.FullName)
        $mainPri = Join-Path $stage 'resources.pri'
        [IO.File]::WriteAllBytes($mainPri, (Read-Entry 'resources.pri'))
        & (Join-Path $PSScriptRoot 'Test-PackageResources.ps1') -PriPath $mainPri -PackageName ([string]$sourceManifest.Package.Identity.Name)

        if (@($names | Where-Object { $_ -match '(?i)(\.(pfx|p12|pem|key|p8|dpapi)$|\.pdb$|\.log$|\.db($|-)|\.sqlite[0-9]?($|-)|cookies?\.(json|txt)$|session\.(json|txt)$|(^|/)(Tests|TestAssets|\.winapp)/)' }).Count) { throw '安装包包含本地数据、私钥或测试文件。' }
        [xml]$manifest = [Text.Encoding]::UTF8.GetString((Read-Entry 'AppxManifest.xml')).TrimStart([char]0xfeff)
        & (Join-Path $PSScriptRoot 'Test-StoreIdentity.ps1') -Manifest $manifest
        if ($manifest.Package.Identity.Name -ne $sourceManifest.Package.Identity.Name -or $manifest.Package.Identity.Version -ne $sourceManifest.Package.Identity.Version -or $manifest.Package.Identity.Publisher -ne $publisher -or $manifest.Package.Identity.ProcessorArchitecture -ne 'x64') { throw '安装包身份与正式日用版不一致。' }
        if ([string]$manifest.Package.Properties.PublisherDisplayName -cne [string]$sourceManifest.Package.Properties.PublisherDisplayName) { throw '安装包的发布者显示名称与源清单不一致。' }
        if (@($manifest.Package.Dependencies.PackageDependency).Where({ $_ }).Count) { throw '自包含安装包仍声明了额外框架依赖。' }
        $assembly = Read-Entry 'Bili_Play.dll'
        $hex = [Convert]::ToHexString($assembly)
        $productionMarker = [Convert]::ToHexString([Text.Encoding]::Unicode.GetBytes('BiliPlay.BilibiliSession'))
        $developmentMarker = [Convert]::ToHexString([Text.Encoding]::Unicode.GetBytes('BiliPlay.BilibiliSession.Development'))
        $testMarker = [Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes('RunVisualCheckAsync'))
        if (-not $hex.Contains($productionMarker) -or $hex.Contains($developmentMarker) -or $hex.Contains($testMarker)) { throw '安装包使用了开发或测试配置。' }
        $profile = [Environment]::GetFolderPath('UserProfile')
        foreach ($name in @('Bili_Play.dll', 'Bili_Play.exe', 'BiliPlay.Core.dll', 'AppxManifest.xml', 'Bili_Play.deps.json', 'Bili_Play.runtimeconfig.json')) {
            $bytes = Read-Entry $name
            $contentHex = [Convert]::ToHexString($bytes)
            if ($contentHex.Contains([Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($profile))) -or $contentHex.Contains([Convert]::ToHexString([Text.Encoding]::Unicode.GetBytes($profile)))) { throw '安装包包含本机用户路径。' }
        }
        foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll')) { if ($name -notin $names) { throw ('缺少自包含运行库：' + $name) } }
        $config = [Text.Encoding]::UTF8.GetString((Read-Entry 'Bili_Play.runtimeconfig.json')).TrimStart([char]0xfeff) | ConvertFrom-Json
        if ($config.runtimeOptions.framework -or $config.runtimeOptions.frameworks) { throw '.NET 运行时未自包含。' }
        # Verify every file block against AppxBlockMap, without trusting or installing the certificate.
        [xml]$blockMap = [Text.Encoding]::UTF8.GetString((Read-Entry 'AppxBlockMap.xml')).TrimStart([char]0xfeff)
        $sha = [Security.Cryptography.SHA256]::Create()
        foreach ($file in $blockMap.BlockMap.File) {
            $data = Read-Entry ($file.Name.Replace('\', '/'))
            if ($data.LongLength -ne [long]$file.Size) { throw '安装包文件大小验证失败。' }
            $offset = 0
            foreach ($block in $file.Block) {
                $count = [Math]::Min(65536, $data.Length - $offset)
                $hash = [Convert]::ToBase64String($sha.ComputeHash($data, $offset, $count))
                if ($hash -ne $block.Hash) { throw '安装包文件哈希验证失败。' }
                $offset += $count
            }
            if ($offset -ne $data.Length) { throw '安装包文件块不完整。' }
        }
        $signature = Read-Entry 'AppxSignature.p7x'
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode([byte[]]$signature[4..($signature.Length - 1)])
        $cms.CheckSignature($true)
        $expectedCert = [IO.File]::ReadAllBytes($publicCert)
        if ([Convert]::ToHexString($cms.SignerInfos[0].Certificate.RawData) -ne [Convert]::ToHexString($expectedCert)) { throw '安装包签名证书不匹配。' }
    } finally { if ($sha) { $sha.Dispose() }; $zip.Dispose() }
    $finalPackage = Join-Path $output ([IO.Path]::GetFileName($package))
    Copy-Item -LiteralPath $package -Destination $finalPackage -Force
    Copy-Item -LiteralPath $publicCert -Destination (Join-Path $output 'BiliPlay.cer') -Force
    Write-Output ('PASS production profile, self-contained runtimes, file hashes, signature and privacy: ' + $finalPackage)
} finally {
    $password = $null
    Pop-Location
    & (Join-Path $PSScriptRoot 'Clean-Generated.ps1') -IncludePublishOutput
}
