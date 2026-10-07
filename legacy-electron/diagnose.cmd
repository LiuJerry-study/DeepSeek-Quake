@echo off
setlocal
cd /d "%~dp0"
set "EXE=%~dp0node_modules\electron\dist\electron.exe"
set "REPORT=%~dp0diagnose-report.txt"

> "%REPORT%" echo ==== DeepSeek Quake diagnose report ====
>> "%REPORT%" echo time : %DATE% %TIME%
>> "%REPORT%" echo user : %USERNAME%
>> "%REPORT%" echo cwd  : %CD%
>> "%REPORT%" echo exe  : %EXE%
if exist "%EXE%" (>> "%REPORT%" echo exe exists: yes) else (>> "%REPORT%" echo exe exists: NO)

>> "%REPORT%" echo.
>> "%REPORT%" echo ---- [1] disk free ----
powershell -NoProfile -Command "Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Used -ne $null } | ForEach-Object { '{0}: free {1} GB' -f $_.Name, [math]::Round($_.Free/1GB,2) }" >> "%REPORT%" 2>&1

>> "%REPORT%" echo.
>> "%REPORT%" echo ---- [2] write test performed by electron.exe itself (ELECTRON_RUN_AS_NODE=1) ----
set "ELECTRON_RUN_AS_NODE=1"
"%EXE%" -e "const fs=require('fs'),p=require('path');const c=[p.join(process.cwd(),'data'),'D:\\Desktop\\dsh-work\\.dsh-tmp\\dsq-probe',p.join(process.env.APPDATA||'','DeepSeek Quake'),p.join(process.env.TEMP||'','DeepSeek Quake')];for(const d of c){try{fs.mkdirSync(d,{recursive:true});const f=p.join(d,'probe.tmp');fs.writeFileSync(f,'ok');fs.unlinkSync(f);console.log('OK   '+d)}catch(e){console.log('FAIL '+d+'  '+(e.code||e.message))}}" >> "%REPORT%" 2>&1
set "ELECTRON_RUN_AS_NODE="

>> "%REPORT%" echo.
>> "%REPORT%" echo ---- [3] single normal launch, output captured, 10s ----
powershell -NoProfile -Command "$o=Join-Path $PWD 'run-out.txt'; $e=Join-Path $PWD 'run-err.txt'; $p=Start-Process -FilePath '%EXE%' -ArgumentList '.','--no-sandbox' -WorkingDirectory $PWD -PassThru -RedirectStandardOutput $o -RedirectStandardError $e; Start-Sleep -Seconds 10; if($p.HasExited){'RESULT: exited, code=' + $p.ExitCode}else{'RESULT: alive after 10s (started ok)'; Stop-Process -Id $p.Id -Force}" >> "%REPORT%" 2>&1
>> "%REPORT%" echo --- stdout ---
if exist "run-out.txt" (type "run-out.txt" >> "%REPORT%") else (>> "%REPORT%" echo none)
>> "%REPORT%" echo --- stderr ---
if exist "run-err.txt" (type "run-err.txt" >> "%REPORT%") else (>> "%REPORT%" echo none)

>> "%REPORT%" echo.
>> "%REPORT%" echo ---- [4] log files ----
if exist "data\deepseek-quake.log" (type "data\deepseek-quake.log" >> "%REPORT%") else (>> "%REPORT%" echo none in appdir\data)
if exist "deepseek-quake.log" (type "deepseek-quake.log" >> "%REPORT%") else (>> "%REPORT%" echo none in appdir)
if exist "%APPDATA%\DeepSeek Quake\deepseek-quake.log" (type "%APPDATA%\DeepSeek Quake\deepseek-quake.log" >> "%REPORT%") else (>> "%REPORT%" echo none in APPDATA)

>> "%REPORT%" echo.
>> "%REPORT%" echo ---- [5] security software and filter drivers ----
powershell -NoProfile -Command "Get-Service | Where-Object { $_.DisplayName -match 'Defender|Sangfor|360|Huorong|Kaspersky|ESET|McAfee' } | ForEach-Object { 'SVC ' + $_.Name + ' : ' + $_.Status }; 'CFA=' + (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access' -ErrorAction SilentlyContinue).EnableControlledFolderAccess; (fltmc filters) 2>&1" >> "%REPORT%" 2>&1

>> "%REPORT%" echo.
>> "%REPORT%" echo done.
del /q "run-out.txt" "run-err.txt" 2>nul

type "%REPORT%"
echo.
echo Report saved to: %REPORT%
pause
