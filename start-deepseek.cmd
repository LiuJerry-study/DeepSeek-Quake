@echo off
rem DeepSeek Quake 一键启动
rem
rem 新版小工具自己会按 hotkey.conf 里的 browser= 去启动浏览器 app 窗口，
rem 所以这里只需要启动小工具；不用先手动开 Chrome。
rem 如果安全软件拦截了这个 cmd，可以直接双击 DeepSeekQuake.exe。
rem
rem %~dp0 = 本文件所在目录，所以整个文件夹可以随便挪。

start "" "%~dp0DeepSeekQuake.exe"
