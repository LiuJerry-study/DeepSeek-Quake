' DeepSeek Quake launcher - starts the client without a console window, and reports
' when it failed to start (otherwise a silent crash just looks like "nothing happened").
' NOTE: keep this file ASCII-only. WScript reads .vbs as ANSI (GBK on zh-CN Windows),
'       so non-ASCII comments here can break string parsing.
Option Explicit
Dim fso, sh, root, exe, wmi, procs
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("WScript.Shell")

root = fso.GetParentFolderName(WScript.ScriptFullName)
exe  = root & "\node_modules\electron\dist\electron.exe"

If Not fso.FileExists(exe) Then
  MsgBox "Dependencies are not installed (electron.exe not found)." & vbCrLf & vbCrLf & _
         "Run this first inside the project folder:" & vbCrLf & "    npm install" & vbCrLf & vbCrLf & root, _
         vbExclamation, "DeepSeek Quake"
  WScript.Quit 1
End If

sh.CurrentDirectory = root
' --no-sandbox is REQUIRED on this machine: the Sangfor SSL VPN client injects
' SangforNspX64.dll into new processes and Chromium's sandbox then fails to initialize,
' killing electron.exe with 0x80000003 before any log is written.
sh.Run """" & exe & """ . --no-sandbox", 0, False

' Wait a moment, then check the process really exists.
WScript.Sleep 6000
On Error Resume Next
Set wmi = GetObject("winmgmts:\\.\root\cimv2")
Set procs = wmi.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='electron.exe'")
If Err.Number = 0 And procs.Count = 0 Then
  MsgBox "DeepSeek Quake did not start." & vbCrLf & vbCrLf & _
         "1) Check the log:" & vbCrLf & _
         "     %APPDATA%\DeepSeek Quake\deepseek-quake.log" & vbCrLf & _
         "     %TEMP%\DeepSeek Quake.log" & vbCrLf & vbCrLf & _
         "2) Or run this in a terminal:" & vbCrLf & _
         "     """ & exe & """ . --no-sandbox" & vbCrLf & vbCrLf & _
         "3) Most common cause: --no-sandbox missing (Sangfor VPN DLL injection makes" & vbCrLf & _
         "   Chromium's sandbox fail; the process dies with 0x80000003 and writes no log).", _
         vbExclamation, "DeepSeek Quake"
End If
On Error GoTo 0
