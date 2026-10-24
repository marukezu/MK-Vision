@echo off
cd /d "%~dp0"
title MK Vision - gerar pasta portatil
rem Monta dist\MK Vision Desktop (o .exe com o .NET embutido: a outra maquina nao precisa instalar nada) e o .zip
rem dela, para testar em outro computador. Sem as DLLs da NVIDIA (4,4 GB): la, a IA de fotos (I), a camera lenta
rem com IA e a de luz e cor ficam desligadas; o player e o navegador funcionam.
set "PASTA=dist\MK Vision Desktop"
echo Gerando a pasta portatil do MK Vision...
if exist "%PASTA%" rmdir /s /q "%PASTA%"
if exist "%PASTA%.zip" del "%PASTA%.zip"
dotnet publish src -c Release -r win-x64 --self-contained true -p:DebugType=none -nologo -v q -clp:ErrorsOnly -o "%PASTA%"
if errorlevel 1 (
  echo.
  echo Deu erro ao gerar. Tire um print desta janela e mande para o Claude.
  pause
  exit /b 1
)
rem A IA de camera lenta treinada com os seus videos fica so neste computador.
del /q "%PASTA%\Models\rife\rife_v4.26_mk.onnx" 2>nul
copy /y "portatil\LEIA-ME.txt" "%PASTA%\LEIA-ME.txt" >nul
echo Compactando...
tar -a -cf "%PASTA%.zip" -C dist "MK Vision Desktop"
if errorlevel 1 (
  echo.
  echo A pasta ficou pronta, mas deu erro ao compactar.
  pause
  exit /b 1
)
echo.
echo Pronto:
echo   pasta: %PASTA%
echo   zip:   %PASTA%.zip
pause
