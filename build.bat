@echo off
rem Builds BloodborneModMenu.exe with the C# compiler that ships with Windows (.NET Framework 4).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:"%~dp0BloodborneModMenu.exe" ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  "%~dp0src\*.cs"
if errorlevel 1 (
  echo.
  echo BUILD FAILED - if the exe is in use, exit Bloodborne Mod Menu from the tray first.
  endlocal & exit /b 1
)
endlocal
