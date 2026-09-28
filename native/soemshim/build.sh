#!/usr/bin/env bash
# 미니PC(Linux) 에서 실행: libsoemshim.so 를 native/soemshim/build/ 에 만든다.
#   sudo apt install -y build-essential cmake git
#   ./build.sh
set -euo pipefail
cd "$(dirname "$0")"

cmake -S . -B build -DCMAKE_BUILD_TYPE=Release "$@"
cmake --build build -j"$(nproc)"

# 실행 파일 옆에서 바로 찾을 수 있도록 결과를 build/ 최상위에 둔다
if [ ! -f build/libsoemshim.so ]; then
  cp "$(find build -name 'libsoemshim.so' | head -n1)" build/libsoemshim.so
fi

echo
echo "built: $(realpath build/libsoemshim.so)"
echo "다음: cd ../../src/EcatMaster.Api && dotnet publish -c Release -o /opt/ecatmaster"
echo "      (csproj 가 build/libsoemshim.so 를 자동으로 출력 폴더에 복사한다)"
