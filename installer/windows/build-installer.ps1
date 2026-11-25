param(
  [string]$Version = "0.2.0",
  [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$WebDir = Join-Path $RepoRoot "web"
$HostWwwRoot = Join-Path $RepoRoot "src\AgentProxyHub.Host\wwwroot"
$WinProj = Join-Path $RepoRoot "src\AgentProxyHub.Desktop.Win\AgentProxyHub.Desktop.Win.csproj"
$DistDir = Join-Path $RepoRoot "dist"
$PublishDir = Join-Path $DistDir "publish-win-x64"
$IssPath = Join-Path $PSScriptRoot "AgentProxyHub.iss"

$IsccCandidates = @(
  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
  "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$Iscc = $IsccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) {
  throw "未找到 Inno Setup 6 编译器 (ISCC.exe)。"
}

Write-Host "[1/5] 编译 Cult UI React 前端..." -ForegroundColor Cyan
pnpm --dir $WebDir install --frozen-lockfile --network-concurrency=2
pnpm --dir $WebDir build

Write-Host "[2/5] 发布 .NET 10 Windows 桌面宿主 (SelfContained=$($SelfContained.IsPresent))..." -ForegroundColor Cyan
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

$scFlag = if ($SelfContained) { "true" } else { "false" }
dotnet publish $WinProj -c Release -r win-x64 --self-contained $scFlag -o $PublishDir

Write-Host "[3/5] 嵌入前端静态资源至发布目录 wwwroot..." -ForegroundColor Cyan
$TargetWwwRoot = Join-Path $PublishDir "wwwroot"
if (Test-Path $TargetWwwRoot) { Remove-Item $TargetWwwRoot -Recurse -Force }
Copy-Item -Path $HostWwwRoot -Destination $TargetWwwRoot -Recurse -Force

Write-Host "[4/5] 使用 Inno Setup 6 编译安装程序..." -ForegroundColor Cyan
$scMode = if ($SelfContained) { "1" } else { "0" }
& $Iscc `
  "/DAppVersion=$Version" `
  "/DPublishDir=$PublishDir" `
  "/DOutputDir=$DistDir" `
  "/DSelfContainedMode=$scMode" `
  $IssPath

if ($LASTEXITCODE -ne 0) {
  throw "ISCC 编译安装程序失败，退出码: $LASTEXITCODE"
}

Write-Host "[5/5] 生成 SHA256SUMS 校验文件..." -ForegroundColor Cyan
$SetupExeName = if ($SelfContained) { "AgentProxyHub-Setup-v$Version-win-x64-standalone.exe" } else { "AgentProxyHub-Setup-v$Version-win-x64.exe" }
$SetupExePath = Join-Path $DistDir $SetupExeName
$Hash = (Get-FileHash $SetupExePath -Algorithm SHA256).Hash.ToLowerInvariant()
$SumFile = Join-Path $DistDir "SHA256SUMS.txt"
"$Hash  $SetupExeName" | Set-Content -Path $SumFile -Encoding utf8

$SizeMb = [Math]::Round((Get-Item $SetupExePath).Length / 1MB, 2)
Write-Host "`n打包成功: $SetupExePath ($SizeMb MB)" -ForegroundColor Green
Write-Host "SHA-256 : $Hash" -ForegroundColor Green
