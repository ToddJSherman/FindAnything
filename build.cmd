@echo off
rem Builds bin\FindAnything.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem Nothing to install. "build test" also builds and runs the test suite.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo C# compiler not found. It comes with .NET Framework 4.x, which is part of Windows 10/11.
    exit /b 1
)
if not exist bin mkdir bin
set REFS=/r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll

rem Windows' built-in OCR and PDF renderer (WinRT), from the metadata that ships with Windows 10/11.
for %%i in ("%CSC%") do set FW=%%~dpi
set WM=%WINDIR%\System32\WinMetadata
set WINRT=/r:"%FW%System.Runtime.dll" /r:"%FW%System.Runtime.WindowsRuntime.dll" /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Media.winmd" /r:"%WM%\Windows.Graphics.winmd" /r:"%WM%\Windows.Storage.winmd" /r:"%WM%\Windows.Data.winmd" /r:"%WM%\Windows.Globalization.winmd"
set REFS=%REFS% %WINRT%

rem The skin: PNG sprites cut from design\ui\sprites.html by tools\make-skin.ps1, plus the icon.
set SKIN=/resource:src\App\app.ico,app.ico
for %%f in (src\App\skin\*.png) do call set SKIN=%%SKIN%% /resource:%%f,skin.%%~nxf

"%CSC%" /nologo /optimize+ /unsafe /target:winexe /out:bin\FindAnything.exe /win32icon:src\App\app.ico /win32manifest:src\App\app.manifest %REFS% %SKIN% src\AppInfo.cs src\Core\*.cs src\App\*.cs
if errorlevel 1 exit /b 1
echo Built bin\FindAnything.exe

rem The installer carries the app and the license inside it.
"%CSC%" /nologo /optimize+ /target:winexe /out:bin\FindAnything-Setup.exe /win32icon:src\App\app.ico /win32manifest:src\App\app.manifest %REFS% /r:Microsoft.CSharp.dll /resource:bin\FindAnything.exe,FindAnything.exe /resource:LICENSE,LICENSE.txt %SKIN% src\AppInfo.cs src\Setup\*.cs src\App\Skin.cs
if errorlevel 1 exit /b 1
echo Built bin\FindAnything-Setup.exe

if /i not "%1"=="test" exit /b 0

"%CSC%" /nologo /optimize+ /unsafe /target:exe /out:bin\tests.exe %REFS% src\Core\*.cs tests\Tests.cs
if errorlevel 1 exit /b 1
set PDFS=%TEMP%\FindAnythingPdfs
python -I tests\make_pdfs.py "%PDFS%" >nul 2>nul
if errorlevel 1 (
    echo Python with PyMuPDF and reportlab not found: skipping the generated-PDF tests.
    bin\tests.exe
) else (
    bin\tests.exe "%PDFS%"
)
