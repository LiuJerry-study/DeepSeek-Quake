@echo off
setlocal
rem DeepSeek Quake һ������
rem
rem �°�С�����Լ��ᰴ hotkey.conf ��� browser= ȥ��������� app ���ڣ�
rem ��������ֻ��Ҫ����С���ߣ��������ֶ��� Chrome��
rem �����ȫ������������� cmd������ֱ��˫�� DeepSeekQuake.exe��
rem
rem %~dp0 = ���ļ�����Ŀ¼�����������ļ��п������Ų��
rem
rem ��Ҫ���ֿⲻ���� exe��.gitignore ���� *.exe�������Ը����� / ��¡ / ��ѹ������
rem �ļ��У���Ŀ¼���ǡ�û�С�DeepSeekQuake.exe �ġ���ֱ��˫����ֻ�ῴ��
rem ��Windows �Ҳ����ļ������������һ�����գ�exe ���ڣ������Զ�����һ����������

set "EXE=%~dp0DeepSeekQuake.exe"
set "BUILD=%~dp0src\build.ps1"

if exist "%EXE%" goto launch

echo [DeepSeek Quake] û�ҵ� DeepSeekQuake.exe�����Զ�����һ��...
echo   ���ֿⲻ���� exe������ȫ�����ص��ļ������Ŀ¼��û�����ģ�
echo.

set "PS=powershell"
where pwsh >nul 2>nul && set "PS=pwsh"

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%BUILD%"
if errorlevel 1 goto buildfail
if not exist "%EXE%" goto buildfail
goto launch

:buildfail
echo.
echo [DeepSeek Quake] �Զ�����û�ɹ����뿴����ı�����
echo   �ֶ����룺�ڱ��ļ�����ִ��  pwsh -File src\build.ps1
echo   ����ԭ�򣺰�ȫ�������أ���ϵͳû�� .NET Framework 4.x ��������
echo.
pause
endlocal
exit /b 1

:launch
start "" "%EXE%"
endlocal
