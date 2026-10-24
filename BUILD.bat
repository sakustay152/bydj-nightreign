@echo off
setlocal
set "HERE=%~dp0"
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. Install .NET Framework 4.x.
    pause
    exit /b 1
)
if not exist "%HERE%bin" mkdir "%HERE%bin"

"%CSC%" -nologo -codepage:65001 -target:winexe -platform:x64 -optimize+ ^
  -out:"%HERE%bin\BydjNightreign.exe" ^
  -r:System.Windows.Forms.dll -r:System.Drawing.dll ^
  "%HERE%src\BydjNightreign.cs" "%HERE%src\GameMemoryAll.cs"

if errorlevel 1 (
    echo [ERROR] Build failed.
    pause
    exit /b 1
)
echo [OK] bin\BydjNightreign.exe
pause
