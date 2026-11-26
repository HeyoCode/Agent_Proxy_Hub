; Agent Proxy Hub — Windows 优雅现代安装向导脚本 (Inno Setup 6)
; 遵循 Cult UI 黑曜石视觉语言与用户级零打扰安装规范

#ifndef AppVersion
  #define AppVersion "0.4.4"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\dist\publish-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif
#ifndef SelfContainedMode
  #define SelfContainedMode "0"
#endif

[Setup]
AppId={{7D91A3B2-4F8C-4E1B-9A2D-6C0E8A5B1F20}
AppName=Agent Proxy Hub
AppVersion={#AppVersion}
AppVerName=Agent Proxy Hub v{#AppVersion}
AppPublisher=Heyo (@HeyoCode)
AppPublisherURL=https://github.com/HeyoCode/Agent_Proxy_Hub
AppSupportURL=https://github.com/HeyoCode/Agent_Proxy_Hub/issues
AppUpdatesURL=https://github.com/HeyoCode/Agent_Proxy_Hub/releases
DefaultDirName={autopf}\AgentProxyHub
DefaultGroupName=Agent Proxy Hub
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousAppDir=yes
CreateUninstallRegKey=not IsPortableMode
UninstallDisplayIcon={app}\AgentProxyHub.Desktop.Win.exe
UninstallDisplayName=Agent Proxy Hub v{#AppVersion}
AppMutex=AgentProxyHub_SingleInstance_Lock
CloseApplications=yes
RestartApplications=no

; Cult UI 现代向导外观与双倍清晰度资产
WizardStyle=modern
WizardSizePercent=115
WizardImageFile=assets\wizard-banner.bmp
WizardSmallImageFile=assets\wizard-small.bmp
WizardImageStretch=yes
WizardImageBackColor=$0D0A0A
SetupIconFile=..\..\assets\app.ico

; 极致压缩与输出命名
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputDir={#OutputDir}
#if SelfContainedMode == "1"
OutputBaseFilename=AgentProxyHub-Setup-v{#AppVersion}-win-x64-standalone
#else
OutputBaseFilename=AgentProxyHub-Setup-v{#AppVersion}-win-x64
#endif

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupWindowTitle=安装 — %1
UninstallAppTitle=卸载 — %1
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=立即安装(&I)
ButtonFinish=完成(&F)
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonNo=否(&N)
WelcomeLabel1=欢迎安装 [name]
WelcomeLabel2=即将在您的计算机上安装 [name/ver]（Claude / ChatGPT / Antigravity 专用独立出口代理与双源用量统计网关）。%n%n• 默认用户级轻量安装，无需系统管理员权限%n• 物理网卡硬件级直绑，彻底绕过 Clash / Wintun 隧道干扰%n• 免解密本地会话统计，零改动系统全局代理%n%n建议关闭正在运行的实例后点击“下一步”继续。
WizardSelectDir=选择安装位置
SelectDirDesc=您希望将 [name] 安装在哪个目录？
SelectDirLabel3=安装程序将把 [name] 安装到以下文件夹中。
SelectDirBrowseLabel=如需更改目录，请点击“浏览(R)...”，随后点击“下一步”。
DiskSpaceMBLabel=至少需要 [mb] MB 可用磁盘空间。
WizardSelectTasks=选择附加任务
SelectTasksDesc=请勾选您希望同时启用的桌面集成选项
SelectTasksLabel2=请选择在安装 [name] 后执行的快捷方式与自启设置：
WizardReady=准备就绪
ReadyLabel1=安装程序已准备好在您的计算机上安装 [name]。
ReadyLabel2a=点击“立即安装”开始写入文件；如需修改安装位置或选项，请点击“上一步”。
WizardInstalling=正在安装...
InstallingLabel=正在将 [name] 与核心隔离组件写入您的计算机，请稍候。
FinishedHeadingLabel=[name] 安装完成
FinishedLabelNoIcons=已完成 [name] 的部署，您可以随时通过开始菜单或快捷方式启动独立代理控制台。
FinishedLabel=已完成 [name] 的安装。勾选下方选项即可立即启动独立出口控制台。
RunEntryExec=立即启动 %1
ClickFinish=点击“完成”退出安装向导。
ExitSetupTitle=退出安装向导
ExitSetupMessage=安装尚未完成，确定要退出 Agent Proxy Hub 安装向导吗？
ConfirmUninstall=确定要从您的计算机彻底卸载 %1 及其程序文件吗？
UninstalledAll=%1 已从您的计算机成功移除。

[Tasks]
Name: "desktopicon"; Description: "在桌面创建“Agent Proxy Hub”快捷方式"; GroupDescription: "桌面与快捷入口:"
Name: "autostart"; Description: "登录 Windows 时静默启动并驻留系统托盘 (--minimized)"; GroupDescription: "后台保活选项:"

[InstallDelete]
Type: filesandordirs; Name: "{app}\wwwroot"
Type: filesandordirs; Name: "{app}\AgentProxyHub.Desktop.Win.exe.WebView2"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Agent Proxy Hub"; Filename: "{app}\AgentProxyHub.Desktop.Win.exe"; IconFilename: "{app}\AgentProxyHub.Desktop.Win.exe"; Comment: "AI 专用独立出口代理与双源用量统计网关"; Check: not IsPortableMode
Name: "{autodesktop}\Agent Proxy Hub"; Filename: "{app}\AgentProxyHub.Desktop.Win.exe"; IconFilename: "{app}\AgentProxyHub.Desktop.Win.exe"; Tasks: desktopicon; Comment: "AI 专用独立出口代理与双源用量统计网关"; Check: not IsPortableMode

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AgentProxyHub"; ValueData: """{app}\AgentProxyHub.Desktop.Win.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart; Check: not IsPortableMode

[Run]
Filename: "{app}\AgentProxyHub.Desktop.Win.exe"; Description: "立即启动 Agent Proxy Hub 控制台"; Flags: nowait postinstall skipifsilent; Check: not IsPortableMode

[Code]
var
  IsSelfContained: Boolean;

function IsPortableMode: Boolean;
begin
  Result := ExpandConstant('{param:PORTABLE|0}') = '1';
end;

function HasWebView2Runtime(): Boolean;
var
  Ver: string;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Ver) or
    RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Ver) or
    DirExists('C:\Program Files (x86)\Microsoft\EdgeWebView\Application');
end;

function HasDotNet10DesktopRuntime(): Boolean;
var
  FindRec: TFindRec;
  BaseDir: string;
begin
  Result := False;
  BaseDir := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(BaseDir) then
    BaseDir := 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App';
  if FindFirst(BaseDir + '\10.*', FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrCode: Integer;
begin
  Result := True;
  IsSelfContained := ('{#SelfContainedMode}' = '1');

  // 非便携模式下，若检测到旧版托盘实例正在运行，先安全终止进程以实现无弹窗平滑覆盖升级
  if (not IsPortableMode) and CheckForMutexes('AgentProxyHub_SingleInstance_Lock') then
  begin
    Exec('taskkill.exe', '/F /IM AgentProxyHub.Desktop.Win.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ErrCode);
    Sleep(400);
  end;

  if IsPortableMode or WizardSilent then
    Exit;

  if not HasWebView2Runtime() then
  begin
    if MsgBox('未检测到 Microsoft Edge WebView2 运行时（Windows 11 通常已自带）。' + #13#10 +
              '是否先打开微软官方 WebView2 下载页面？（选择“否”仍可继续完成安装）',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://developer.microsoft.com/en-us/microsoft-edge/webview2/', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
    end;
  end;

  if (not IsSelfContained) and (not HasDotNet10DesktopRuntime()) then
  begin
    if MsgBox('当前为轻量版安装包，系统尚未检测到 .NET 10 Desktop Runtime (x64)。' + #13#10 +
              '是否打开微软 .NET 10 下载页面？（选择“否”可先完成安装）',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{%USERPROFILE}\.agent-proxy-hub');
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时清理本地保存的代理节点与历史配置 (' + DataDir + ')？' + #13#10 +
                '（建议选择“否”以保留您的代理线路配置，方便重装或升级后直接沿用）',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
