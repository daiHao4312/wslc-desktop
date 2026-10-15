@echo off
chcp 936 >nul
setlocal EnableExtensions

rem ============================================================
rem  WSLC 面板 - 启动器
rem
rem  用法：
rem    启动.bat          直接启动已构建的应用
rem    启动.bat build    先重新构建，再启动
rem
rem  注意：本文件为 GBK(ANSI) 编码，请勿用 UTF-8 保存，
rem        否则中文提示会变乱码，并可能破坏脚本解析。
rem        目录用 %~dp0 自解析，脚本内不含任何硬编码路径。
rem ============================================================

set "ROOT=%~dp0"
set "EXE=%ROOT%src\WSLC.Panel.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\WSLC.Panel.App.exe"

if /I "%~1"=="build" goto :build
if not exist "%EXE%" goto :build
goto :run

:build
echo [1/2] 构建解决方案（Debug）...
echo.
dotnet build "%ROOT%WSLC-Panel.slnx" -c Debug
if errorlevel 1 (
    echo.
    echo [失败] 构建未通过，已中止，应用不会启动。
    pause
    exit /b 1
)
echo.

:run
if not exist "%EXE%" (
    echo [失败] 找不到可执行文件：
    echo        %EXE%
    pause
    exit /b 1
)

echo [2/2] 启动 WSLC 面板...
start "" "%EXE%"
echo       已发出启动请求，窗口或托盘图标随后出现。
echo.
echo 提示：
echo   - 关闭按钮默认最小化到托盘，不是退出；退出走托盘图标右键菜单。
echo   - 重复运行本脚本不会开第二个窗口，只会把已有窗口唤到前台。
echo   - 异常日志：%LOCALAPPDATA%\WSLCPanel\error.log

ping -n 3 127.0.0.1 >nul 2>&1
exit /b 0
