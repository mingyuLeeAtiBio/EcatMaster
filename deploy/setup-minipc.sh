#!/usr/bin/env bash
# N100 미니PC (Ubuntu 24.04 / Debian 12) 초기 설정.
# 사용:  sudo ./setup-minipc.sh <ethercat-nic>   예) sudo ./setup-minipc.sh enp2s0
set -euo pipefail

ECAT_IF="${1:-}"
if [ -z "$ECAT_IF" ]; then
  echo "usage: $0 <ethercat-nic>"; ip -br link; exit 1
fi

echo "== 1. 패키지"
apt-get update
apt-get install -y build-essential cmake git ethtool linux-image-rt-amd64 2>/dev/null \
  || apt-get install -y build-essential cmake git ethtool linux-image-realtime 2>/dev/null \
  || echo "!! RT 커널 패키지를 자동으로 찾지 못했습니다. Ubuntu 는 'pro attach' 후 'pro enable realtime-kernel' 을 쓰세요."

echo "== 2. .NET 9 런타임"
if ! command -v dotnet >/dev/null; then
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0 --runtime aspnetcore --install-dir /usr/share/dotnet
  ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
fi

echo "== 3. 서비스 계정"
id -u ecatmaster >/dev/null 2>&1 || useradd -r -s /usr/sbin/nologin -G dialout ecatmaster
mkdir -p /opt/ecatmaster && chown ecatmaster:ecatmaster /opt/ecatmaster

echo "== 4. EtherCAT NIC 를 네트워크 매니저에서 제외 (IP 없이 raw 로만 사용)"
if command -v nmcli >/dev/null; then
  nmcli device set "$ECAT_IF" managed no || true
fi
cat >/etc/systemd/network/10-ecat-"$ECAT_IF".link <<EOF
[Match]
OriginalName=$ECAT_IF
[Link]
# 오프로드/EEE 는 지터를 키운다
GenericSegmentationOffload=false
TCPSegmentationOffload=false
GenericReceiveOffload=false
EOF
ethtool -K "$ECAT_IF" gro off gso off tso off 2>/dev/null || true
ethtool --set-eee "$ECAT_IF" eee off 2>/dev/null || true
ip link set "$ECAT_IF" up || true

echo "== 5. 커널 파라미터 (코어 3 을 EtherCAT 전용으로 격리, C-state 제한)"
GRUB_ADD="isolcpus=3 nohz_full=3 rcu_nocbs=3 intel_idle.max_cstate=1 processor.max_cstate=1 idle=poll"
if ! grep -q isolcpus /etc/default/grub; then
  sed -i "s/^GRUB_CMDLINE_LINUX_DEFAULT=\"\(.*\)\"/GRUB_CMDLINE_LINUX_DEFAULT=\"\1 $GRUB_ADD\"/" /etc/default/grub
  update-grub
  echo "   -> 재부팅 후 적용. appsettings.json 의 Master:CpuCore 를 3 으로 두세요."
fi

echo "== 6. 실시간 권한"
cat >/etc/security/limits.d/99-realtime.conf <<EOF
@ecatmaster   -  rtprio   99
@ecatmaster   -  memlock  unlimited
EOF

echo
echo "완료. 다음 단계:"
echo "  1) 재부팅 후 'uname -a' 로 PREEMPT_RT 커널 확인"
echo "  2) native/soemshim/build.sh 실행 → libsoemshim.so"
echo "  3) src/EcatMaster.Api 에서 dotnet publish -c Release -r linux-x64 --self-contained false -o /opt/ecatmaster"
echo "  4) appsettings.json 의 Master:Interface=$ECAT_IF, Mode=Soem 확인"
echo "  5) cp deploy/ecatmaster.service /etc/systemd/system/ && systemctl enable --now ecatmaster"
echo "  6) http://<minipc-ip>:5000/swagger"
