@echo off
rem 用 Windows 自带的 .NET Framework 编译器生成 WDS转谱器.exe
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo 找不到 csc.exe,请安装 .NET Framework 4.x
  exit /b 1
)
"%CSC%" /nologo /target:winexe /codepage:65001 /out:"%~dp0WDS转谱器.exe" ^
        /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0src\ChartForge.cs"
if errorlevel 1 (
  echo 编译失败
  exit /b 1
)
echo 编译完成: %~dp0WDS转谱器.exe
