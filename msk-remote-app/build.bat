@echo off
setlocal
echo =========================================================
echo   Compiling VNC Remote Connect Native Desktop App
echo =========================================================

set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist %CSC% (
    set CSC="C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

if not exist bin mkdir bin

echo Using C# Compiler: %CSC%
%CSC% /nologo /target:winexe /optimize+ /platform:anycpu ^
    /lib:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF" ^
    /r:PresentationFramework.dll ^
    /r:PresentationCore.dll ^
    /r:WindowsBase.dll ^
    /r:System.Xaml.dll ^
    /r:System.dll ^
    /r:System.Core.dll ^
    /r:System.Web.Extensions.dll ^
    /out:"bin\VNCremoteconnect.exe" ^
    "src\MainWindow.cs"

if %ERRORLEVEL% equ 0 (
    echo [SUCCESS] VNCremoteconnect.exe compiled successfully!
    dir bin\VNCremoteconnect.exe
) else (
    echo [FAILED] Compilation failed with error code %ERRORLEVEL%
)

endlocal
