@echo off
rem DeepSeek Quake 一键启动
rem
rem 新版小工具自己会按 hotkey.conf 里的 browser= 去启动浏览器 app 窗口，
rem 所以这里只需要启动小工具；不用先手动开 Chrome。
rem 如果安全软件拦截了这个 cmd，可以直接双击 DeepSeekQuake.exe。
rem 想手动先开浏览器窗口的话，双击「DeepSeek Chrome.lnk」。

start "" "D:\Desktop\DeepSeek Quake\DeepSeekQuake.exe"
