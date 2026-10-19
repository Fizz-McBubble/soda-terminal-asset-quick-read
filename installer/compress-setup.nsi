; Adapted from the reviewed scanner NSIS 3.13 wrapper; UI precedes the solid payload.
Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 96
ReserveFile /plugin System.dll
Name "Soda Terminal · 资产快读"
Icon "soda-brand-icon.ico"
OutFile "${OUTPUT_EXE}"
RequestExecutionLevel user
SilentInstall silent
CRCCheck force
ManifestDPIAware true
!ifndef INSTALLER_VERSION
  !error "INSTALLER_VERSION is required"
!endif
VIProductVersion "${INSTALLER_VERSION}.0"
VIAddVersionKey "ProductName" "Soda Terminal · 资产快读"
VIAddVersionKey "FileDescription" "资产快读独立安装程序"
VIAddVersionKey "FileVersion" "${INSTALLER_VERSION}"
VIAddVersionKey "ProductVersion" "${INSTALLER_VERSION}"
VIAddVersionKey "LegalCopyright" "Soda Terminal contributors; NSIS contributors"
!include "FileFunc.nsh"
Section
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=Soda.AssetQuickRead.Setup.exe "${INNER_EXE}"
  ${GetParameters} $0
  StrCpy $8 '"$PLUGINSDIR\Soda.AssetQuickRead.Setup.exe" $0'
  System::Alloc 68
  Pop $1
  System::Call '*$1(i 68)'
  System::Alloc 16
  Pop $2
  System::Call 'kernel32::CreateProcessW(p 0, w r8, p 0, p 0, i 0, i 0, p 0, p 0, p r1, p r2) i.r3'
  IntCmp $3 0 launch_failed
  System::Call '*$2(p .r4, p .r5, i .r6, i .r7)'
  System::Call 'kernel32::CloseHandle(p r5)'
  System::Free $1
  System::Free $2
  SetOutPath "$PLUGINSDIR\payload"
  ClearErrors
  File /oname=uninstaller.exe "${UNINSTALLER_EXE}"
  IfErrors extract_failed
  ClearErrors
  File /oname=asset-quick-read.zip "${RUNTIME_ARCHIVE}"
  IfErrors extract_failed
  FileOpen $1 "$PLUGINSDIR\payload\ready.tmp" w
  IfErrors extract_failed
  FileWrite $1 "SODA-ASSET-QUICK-READ-PAYLOAD-1"
  FileClose $1
  Rename "$PLUGINSDIR\payload\ready.tmp" "$PLUGINSDIR\payload\ready"
  IfErrors extract_failed
  Goto wait_child
extract_failed:
  FileOpen $1 "$PLUGINSDIR\payload\failed" w
  FileClose $1
wait_child:
  System::Call 'kernel32::WaitForSingleObject(p r4, i -1) i.r3'
  System::Call 'kernel32::GetExitCodeProcess(p r4, *i .r3)'
  System::Call 'kernel32::CloseHandle(p r4)'
  SetErrorLevel $3
  Quit
launch_failed:
  System::Free $1
  System::Free $2
  SetErrorLevel 2
SectionEnd
