@echo off
cd /d "%~dp0"
title MK Vision
rem Compila (se houver mudancas) e abre o MK Vision. Arraste um video sobre este arquivo para abrir direto nele.
echo Preparando o MK Vision...
dotnet build src -c Release -nologo -v q -clp:ErrorsOnly
if errorlevel 1 (
  rem Com o MK Vision aberto a compilacao nao consegue trocar o programa: so traz a janela para a frente.
  tasklist /fi "imagename eq MKVisionDesktop.exe" | find /i "MKVisionDesktop.exe" >nul && goto abrir
  echo.
  echo Deu erro ao compilar. Tire um print desta janela e mande para o Claude.
  pause
  exit /b 1
)
:abrir
start "" "src\bin\Release\net10.0-windows10.0.22621.0\MKVisionDesktop.exe" %*
