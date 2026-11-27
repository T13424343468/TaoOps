; TaoOps Windows 版安装器（FR-A31，按定版原型 v1.3）
; 默认目录 D:\Program Files\TaoOps（可改，输错路径由向导校验）
; 卸载完全清理：服务、自启、注册表、防火墙规则、程序文件；数据目录可选保留。

#define MyAppName "TaoOps"
#define MyAppVersion "1.0.0"
#define MyAppService "TaoOps"

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=TaoOps
DefaultDirName=D:\Program Files\TaoOps
DirExistsWarning=no
AppendDefaultDirName=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputBaseFilename=TaoOps-Setup-{#MyAppVersion}
; 卸载后如目录非空（保留的数据目录），不强制删
UninstallFilesDir={app}\unins

[Files]
; 中控自包含发布产物（scripts/publish-windows.sh 产出）
Source: "dist\windows\TaoOps\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\TaoOps"; Filename: "{app}\TaoOps.Server.exe"

[Run]
; 防火墙放行面板端口（TCP 5241）
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""TaoOps"" dir=in action=allow protocol=TCP localport=5241"; Flags: runhidden
; 注册 Windows 服务并设为自启
Filename: "sc.exe"; Parameters: "create {#MyAppService} binPath= ""{app}\TaoOps.Server.exe"" start= auto DisplayName= ""TaoOps Server"""; Flags: runhidden
Filename: "sc.exe"; Parameters: "description {#MyAppService} ""TaoOps 服务器管理面板中控"""; Flags: runhidden
Filename: "sc.exe"; Parameters: "start {#MyAppService}"; Flags: runhidden
; 安装完成打开面板
Filename: "{app}\TaoOps.Server.exe"; Description: "打开 TaoOps 面板"; Flags: postinstall skipifsilent nowait

[UninstallRun]
; —— 完全卸载（按定版原型：六项全清）——
; 1. 停止并删除 Windows 服务
Filename: "sc.exe"; Parameters: "stop {#MyAppService}"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "sc.exe"; Parameters: "delete {#MyAppService}"; Flags: runhidden; RunOnceId: "DelSvc"
; 2. 删除开机自启残留（计划任务，若存在）
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""TaoOps"" /F"; Flags: runhidden; RunOnceId: "DelTask"
; 3. 删除防火墙放行规则
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""TaoOps"""; Flags: runhidden; RunOnceId: "DelFw"

[Code]
// 卸载前询问：数据目录删不删（原型定版口径：想留历史数据就保留）
function InitializeUninstall(): Boolean;
var
  DataDir: string;
  Choice: Integer;
begin
  DataDir := ExpandConstant('{commonappdata}') + '\TaoOps\data';
  if DirExists(DataDir) then
  begin
    Choice := MsgBox('是否同时删除历史数据目录？' #13#10 +
      DataDir #13#10 #13#10 +
      '【是】完全清除（不可恢复）    【否】保留数据，重装后接着用',
      mbConfirmation, MB_YESNOCANCEL);
    if Choice = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
    if Choice = IDYES then
      RegWriteString(HKEY_LOCAL_MACHINE, 'SOFTWARE\TaoOps', 'DeleteDataOnUninstall', '1')
    else
      RegDeleteValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\TaoOps', 'DeleteDataOnUninstall');
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
  DeleteData: Boolean;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // 卸载完成后：按标记处理数据目录，再清掉 TaoOps 相关注册表项
    DataDir := ExpandConstant('{commonappdata}') + '\TaoOps\data';
    DeleteData := RegValueExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\TaoOps', 'DeleteDataOnUninstall');
    if DeleteData then
      DelTree(DataDir, True, True, True);
    RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE, 'SOFTWARE\TaoOps');
  end;
end;
