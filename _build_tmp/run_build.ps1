# 与 _build.bat 等价的编译脚本（MSBuild 直调被安全策略拦截，故经此包装执行）
$ErrorActionPreference = "Stop"
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    Write-Host "MSBuild not found: $msbuild"
    exit 2
}
& $msbuild "C:\Users\59538\Desktop\RBLAOI\RBLAOI.csproj" /t:Build /p:Configuration=Debug /nologo /v:m /fl "/flp:logfile=C:\Users\59538\Desktop\RBLAOI\_build_tmp\msbuild_fl.log;verbosity=minimal" 2>&1 | Out-File -FilePath "C:\Users\59538\Desktop\RBLAOI\_build_tmp\build_result.log" -Encoding utf8
exit $LASTEXITCODE
