@echo off
chcp 65001 >nul
"C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\59538\Desktop\RBLAOI\RBLAOI.csproj" /t:Build /p:Configuration=Debug /nologo /v:minimal
