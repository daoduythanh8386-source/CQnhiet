; Bộ cài CQ Nhiệt Luyện — Windows x64. Biên dịch: ISCC.exe /DAppVersion=x.y.z /DPublishDir=<thư mục publish> CQNhietLuyen.iss
; Gỡ cài đặt KHÔNG xóa dữ liệu: %LocalAppData%\CQNhietLuyen (cấu hình, nháp offline, bản sao) và kho dữ liệu chung (thư mục Synology Drive).
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
; Đường dẫn kho chung gợi ý ở trang "Kho dữ liệu chung" (máy đã cài trước đó: dùng lại đường dẫn đã nhập lần trước)
#ifndef DefaultStore
  #define DefaultStore "%USERPROFILE%\SynologyDrive\DU LIEU NHIET"
#endif

[Setup]
AppId={{7C3F6B0E-2D8A-4E55-9B1C-3A51C0E1D001}
AppName=CQ Nhiệt Luyện
AppVersion={#AppVersion}
AppVerName=CQ Nhiệt Luyện {#AppVersion}
AppPublisher=Đào Duy Thanh
AppCopyright=© Đào Duy Thanh. Mọi quyền được bảo lưu.
DefaultDirName={autopf}\CQ Nhiet Luyen
DefaultGroupName=CQ Nhiệt Luyện
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\installer_out
OutputBaseFilename=CQNhietLuyen-Setup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=CQ Nhiệt Luyện
UninstallDisplayIcon={app}\CQNhietLuyen.exe
SetupIconFile=..\src\CqNl.Wpf\Assets\app.ico
WizardImageFile=wizard_large.bmp
WizardSmallImageFile=wizard_small.bmp
CloseApplications=force
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=CQ Nhiệt Luyện
VersionInfoCompany=Đào Duy Thanh

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"


#ifndef NoViMessages
; Giao diện trình cài đặt bằng tiếng Việt (ghi đè thông điệp của ngôn ngữ mặc định)
[Messages]
SetupAppTitle=Cài đặt
SetupWindowTitle=Cài đặt - %1
UninstallAppTitle=Gỡ cài đặt
UninstallAppFullTitle=Gỡ cài đặt %1
ButtonBack=< &Quay lại
ButtonNext=&Tiếp >
ButtonInstall=&Cài đặt
ButtonOK=Đồng ý
ButtonCancel=Hủy
ButtonYes=&Có
ButtonNo=&Không
ButtonFinish=&Hoàn tất
ButtonBrowse=&Chọn thư mục...
ButtonWizardBrowse=Chọn &thư mục...
ExitSetupTitle=Thoát cài đặt
ExitSetupMessage=Cài đặt chưa xong. Nếu thoát bây giờ, chương trình sẽ không được cài.%n%nBạn có thể chạy lại bộ cài sau.%n%nThoát cài đặt?
WelcomeLabel1=Chào mừng đến với trình cài đặt [name]
WelcomeLabel2=Trình cài đặt sẽ cài [name/ver] lên máy tính của bạn.%n%nNên đóng các ứng dụng khác trước khi tiếp tục.
WizardSelectDir=Chọn thư mục cài đặt
SelectDirDesc=Cài [name] vào đâu?
SelectDirLabel3=Trình cài đặt sẽ cài [name] vào thư mục sau.
SelectDirBrowseLabel=Bấm Tiếp để tiếp tục. Muốn chọn thư mục khác, bấm Chọn thư mục.
WizardSelectTasks=Chọn tác vụ bổ sung
SelectTasksDesc=Thực hiện thêm tác vụ nào?
SelectTasksLabel2=Chọn các tác vụ bổ sung khi cài [name], rồi bấm Tiếp.
WizardReady=Sẵn sàng cài đặt
ReadyLabel1=Trình cài đặt đã sẵn sàng cài [name] lên máy tính.
ReadyLabel2a=Bấm Cài đặt để tiếp tục, hoặc Quay lại để xem/đổi lựa chọn.
ReadyMemoDir=Thư mục cài đặt:
ReadyMemoTasks=Tác vụ bổ sung:
WizardInstalling=Đang cài đặt
InstallingLabel=Vui lòng chờ trong khi cài [name] lên máy tính.
FinishedHeadingLabel=Hoàn tất cài đặt [name]
FinishedLabelNoIcons=Đã cài xong [name] trên máy tính.
FinishedLabel=Đã cài xong [name] trên máy tính. Mở ứng dụng bằng biểu tượng vừa tạo.
ClickFinish=Bấm Hoàn tất để thoát.
RunEntryExec=Mở %1
ConfirmUninstall=Gỡ %1 khỏi máy tính?%n%nDữ liệu (kho chung và dữ liệu trên máy này) được GIỮ NGUYÊN.
UninstalledAll=Đã gỡ %1 khỏi máy tính. Dữ liệu được giữ nguyên.
PrivilegesRequiredOverrideTitle=Chọn chế độ cài đặt
PrivilegesRequiredOverrideInstruction=Chọn chế độ cài đặt
PrivilegesRequiredOverrideText1=%1 có thể cài cho mọi người dùng (cần quyền quản trị) hoặc chỉ cho bạn.
PrivilegesRequiredOverrideText2=%1 có thể cài chỉ cho bạn hoặc cho mọi người dùng (cần quyền quản trị).
PrivilegesRequiredOverrideAllUsers=Cài cho &mọi người dùng
PrivilegesRequiredOverrideAllUsersRecommended=Cài cho &mọi người dùng (khuyến nghị)
PrivilegesRequiredOverrideCurrentUser=Chỉ cài cho &tôi
PrivilegesRequiredOverrideCurrentUserRecommended=Chỉ cài cho &tôi (khuyến nghị)
#endif

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng trên màn hình Desktop"; GroupDescription: "Biểu tượng:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; script kết nối kho chung cho máy trạm + cấu hình máy chủ, hướng dẫn (nằm trong thư mục cài, mục "tools")
Source: "..\tools\*.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\docs\HUONG_DAN_MAY_CHU_VA_CAI_DAT.md"; DestDir: "{app}\tools"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\CQ Nhiệt Luyện"; Filename: "{app}\CQNhietLuyen.exe"
Name: "{autodesktop}\CQ Nhiệt Luyện"; Filename: "{app}\CQNhietLuyen.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CQNhietLuyen.exe"; Description: "Mở CQ Nhiệt Luyện"; Flags: nowait postinstall skipifsilent
; Cập nhật từ trong ứng dụng: bộ cài chạy im lặng với /RELAUNCH → cài xong tự mở lại ứng dụng (dưới tài khoản Windows của người dùng)
Filename: "{app}\CQNhietLuyen.exe"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

; Không có [UninstallDelete]: gỡ cài đặt chỉ xóa file chương trình đã cài; dữ liệu người dùng và kho chung được giữ nguyên.

[Code]
// Trước khi chép file: đóng hẳn app CQ Nhiệt Luyện đang mở (mọi phiên của người dùng này) → không hiện màn hình
// "applications are using files" và không bị huỷ cài khi chạy im lặng (lỗi gặp 17/11/2026). Dữ liệu đã lưu trên kho chung.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM CQNhietLuyen.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1500);
  Result := '';
end;

// ---- Trang "Kho dữ liệu chung" (0.9.2): mỗi máy dùng BẢN ĐỒNG BỘ Synology Drive của thư mục DU LIEU NHIET (NAS edoc.oristar.vn,
// thư mục nhóm TQM HƯNG YÊN) → app chạy độc lập, ADMIN-PC tắt vẫn dùng được. Mặc định %USERPROFILE%\SynologyDrive\DU LIEU NHIET
// (giữ nguyên chữ %USERPROFILE% trong store.txt — app tự hiểu theo tài khoản Windows đang dùng).
// Ghi vào {app}\store.txt; app đọc khi mở và dùng đường dẫn này (chỉ khi khác lần áp dụng trước — không đè lựa chọn đổi sau đó trong app).
// Cài im lặng (cập nhật tự động trong app): không ghi lại, trừ khi truyền /STORE="..."
var
  StorePage: TInputQueryWizardPage;

function ExpandStore(V: String): String;
begin
  Result := V;
  StringChangeEx(Result, '%USERPROFILE%', GetEnv('USERPROFILE'), True);
end;

procedure InitializeWizard;
var
  Prev: String;
begin
  StorePage := CreateInputQueryPage(wpSelectDir, 'Kho dữ liệu chung',
    'Thư mục DU LIEU NHIET do Synology Drive đồng bộ về máy này',
    'Mỗi máy cài Synology Drive Client, đăng nhập NAS edoc.oristar.vn và đồng bộ thư mục nhóm TQM HƯNG YÊN (có DU LIEU NHIET) về máy. ' +
    'App dùng bản đồng bộ trên máy mình: máy ADMIN-PC tắt vẫn dùng được, chỉ cần internet để đồng bộ với các máy khác.' + #13#10 + #13#10 +
    'Thường giữ nguyên đường dẫn mặc định. Đổi được sau trong ứng dụng (màn Đăng nhập → Đổi đường dẫn kho).');
  StorePage.Add('Đường dẫn kho chung:', False);
  Prev := GetPreviousData('StorePath', '{#DefaultStore}');
  // bản cài trước 0.9.2 trỏ về ổ của ADMIN-PC qua mạng LAN → đề nghị chuyển sang bản đồng bộ trên máy này
  if Pos('\\ADMIN-PC\', Uppercase(Prev)) = 1 then Prev := '{#DefaultStore}';
  StorePage.Values[0] := Prev;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'StorePath', Trim(StorePage.Values[0]));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  V, E: String;
begin
  Result := True;
  if CurPageID = StorePage.ID then
  begin
    V := Trim(StorePage.Values[0]);
    if V = '' then
    begin
      MsgBox('Nhập đường dẫn kho chung (mặc định {#DefaultStore}).', mbError, MB_OK);
      Result := False; exit;
    end;
    E := ExpandStore(V);
    if not FileExists(AddBackslash(E) + 'CQNL\store.json') then
      if MsgBox('Máy này CHƯA có dữ liệu kho tại:' + #13#10 + E + #13#10 + #13#10 +
                'Cần: cài Synology Drive Client, đăng nhập NAS, đồng bộ thư mục TQM HƯNG YÊN (có DU LIEU NHIET) và chờ đồng bộ xong. ' +
                'Nên đặt thư mục DU LIEU NHIET ở chế độ "Luôn giữ trên thiết bị này".' + #13#10 + #13#10 +
                'Vẫn cài tiếp (app sẽ chờ đồng bộ xong mới cho dùng)?', mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine + 'Kho dữ liệu chung:' + NewLine + Space + Trim(StorePage.Values[0]);
  if MemoTasksInfo <> '' then Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  V: String;
  Lines: TArrayOfString;
begin
  if CurStep = ssPostInstall then
  begin
    V := Trim(ExpandConstant('{param:STORE|}'));
    if (V = '') and (not WizardSilent) then V := Trim(StorePage.Values[0]);
    if V <> '' then
    begin
      SetArrayLength(Lines, 1);
      Lines[0] := V;
      SaveStringsToUTF8File(ExpandConstant('{app}\store.txt'), Lines, False);
    end;
  end;
end;

function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (Pos('/RELAUNCH', Uppercase(GetCmdTail)) > 0);
end;

