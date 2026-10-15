@echo off
chcp 936 >nul
setlocal EnableExtensions

rem ============================================================
rem  WSLC 面板 - 测试
rem
rem  用法：
rem    跑测试.bat          跑全部测试（含 Live* 实机联调）
rem    跑测试.bat unit     只跑纯单元测试，跳过 Live* 实机联调
rem
rem  注意：本文件为 GBK(ANSI) 编码，请勿用 UTF-8 保存。
rem        Live* 测试需要真实 wslc 引擎；引擎不可用时它们会自行
rem        跳过并记日志，不会让套件变红，但要额外花十几秒探测，
rem        所以日常改代码用 unit 更快。
rem ============================================================

set "ROOT=%~dp0"
set "TESTS=%ROOT%tests\WSLC.Panel.Core.Tests\WSLC.Panel.Core.Tests.csproj"

if /I "%~1"=="unit" (
    echo 运行单元测试（跳过 Live* 实机联调）...
    echo.
    dotnet test "%TESTS%" -c Debug --filter "FullyQualifiedName!~Live" --logger "console;verbosity=normal"
) else (
    echo 运行全部测试（含 Live* 实机联调）...
    echo.
    dotnet test "%TESTS%" -c Debug --logger "console;verbosity=normal"
)

set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" (
    echo ============================================
    echo  通过：测试套件全绿。
    echo ============================================
) else (
    echo ============================================
    echo  失败：存在未通过用例，退出码 %RC%
    echo ============================================
)

pause
exit /b %RC%
