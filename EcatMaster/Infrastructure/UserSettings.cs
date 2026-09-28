using System.IO;
using System.Text.Json;

namespace EcatMaster.Infrastructure;

/// <summary>GUI 입력값을 %LOCALAPPDATA%\EcatMaster\settings.json 에 저장해 다음 실행 때 복원한다.</summary>
public sealed class UserSettings
{
    /// <summary>설정 파일 형식 버전. 단위 의미가 바뀐 구버전 파일은 모션 입력값을 기본값으로 되돌린다.</summary>
    public const int CurrentVersion = 2;
    /// <summary>역직렬화 시 키가 없으면 0 → 구버전으로 판정. 저장할 때 CurrentVersion 으로 기록.</summary>
    public int Version { get; set; }

    public bool UseSimulation { get; set; }
    public string? AdapterName { get; set; }
    /// <summary>Start-up: probe NICs for EtherCAT slaves and connect automatically (single hit) or ask (several hits).</summary>
    public bool AutoConnect { get; set; } = true;
    public int CycleTimeUs { get; set; } = 2000;
    public int AxisCount { get; set; } = 12;
    public bool RemapPdo { get; set; } = true;
    public int CpuCore { get; set; } = -1;

    /// <summary>DC 동기 드라이브용: 마스터 프레임을 SYNC0 보다 이만큼 앞선 위상에 맞춘다 [µs]. Windows 는 지터가 커서 500 권장.</summary>
    public int DcSyncOffsetUs { get; set; } = 500;

    /// <summary>"step" | "um"</summary>
    public string Unit { get; set; } = "um";

    /// <summary>출력축 1회전당 이동량 [µm] (모든 축 공통 기본값)</summary>
    public double LeadUm { get; set; } = 10_000;

    // 입력값은 현재 단위 기준
    public double TargetPosition { get; set; } = 1000;
    public double Velocity { get; set; } = 1000;
    public double Acceleration { get; set; } = 300_000;
    public double Deceleration { get; set; } = 300_000;
    public double JogVelocity { get; set; } = 500;

    // 왕복 테스트 (현재 단위 기준)
    public double CyclePointA { get; set; } = 0;
    public double CyclePointB { get; set; } = 10_000;
    public double CycleVelocity { get; set; } = 5_000;
    public int CycleCount { get; set; } = 10;
    public int CycleDwellMs { get; set; } = 200;

    private static readonly string PathOnDisk =
        AppPaths.SettingsFile;   // %LOCALAPPDATA%/EcatMaster/settings.json (구 DirectControl 폴더에서 자동 이전)

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
            {
                var loaded = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(PathOnDisk), JsonOpts) ?? new UserSettings();
                if (loaded.Version < CurrentVersion)
                {
                    // 구버전(step/Hz/kHz·s 입력) 파일: 모션 입력값만 새 기본값으로 되돌리고 연결 설정은 유지
                    var fresh = new UserSettings();
                    loaded.Unit = fresh.Unit;
                    loaded.LeadUm = fresh.LeadUm;
                    loaded.TargetPosition = fresh.TargetPosition;
                    loaded.Velocity = fresh.Velocity;
                    loaded.Acceleration = fresh.Acceleration;
                    loaded.Deceleration = fresh.Deceleration;
                    loaded.JogVelocity = fresh.JogVelocity;
                    loaded.Version = CurrentVersion;
                }
                return loaded;
            }
        }
        catch { /* 손상된 파일은 기본값으로 */ }
        return new UserSettings();
    }

    public void Save()
    {
        try
        {
            Version = CurrentVersion;
            Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
            File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* 저장 실패는 무시 (읽기 전용 위치 등) */ }
    }
}
