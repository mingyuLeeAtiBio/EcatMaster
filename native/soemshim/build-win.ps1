# Windows 개발 PC 에서 soemshim.dll (x64) 을 만든다.
#   요구: Visual Studio 2022 (C++ 데스크톱 워크로드), CMake (VS 에 포함), Git
#   실행 시: Npcap 설치 (https://npcap.com, "WinPcap API-compatible Mode" 체크)
#   사용:  powershell -ExecutionPolicy Bypass -File build-win.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $cands = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\2022\*\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" -ErrorAction SilentlyContinue
    if ($cands) { $cmake = $cands[0].FullName } else { throw "cmake.exe 를 찾을 수 없습니다" }
}

& $cmake -S . -B build-win -G "Visual Studio 17 2022" -A x64
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }
& $cmake --build build-win --config Release
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

Copy-Item build-win\Release\soemshim.dll build-win\soemshim.dll -Force
Write-Host ""
Write-Host "built: $(Resolve-Path build-win\soemshim.dll)"
Write-Host "WPF 프로젝트(EcatMaster.csproj) 가 빌드 시 이 DLL 을 출력 폴더로 복사합니다."
