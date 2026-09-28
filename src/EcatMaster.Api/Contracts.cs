namespace EcatMaster.Api;

/// <summary>
/// 절대/상대 위치 이동. 값의 단위는 <paramref name="Unit"/>("step" | "um"), 생략 시 서버 설정(Master:Units:Mode).
/// um 이면 위치 µm, 속도 µm/s, 가감속 µm/s². 생략한 프로파일 값은 드라이브에서 읽은 기본값을 쓴다.
/// </summary>
public sealed record MoveRequest(double Position, double? Velocity = null, double? Acceleration = null, double? Deceleration = null, string? Unit = null);

/// <summary>조그(PV 모드). Velocity 부호가 방향. 단위는 Unit 에 따름.</summary>
public sealed record JogRequest(double Velocity, double? Acceleration = null, double? Deceleration = null, string? Unit = null);

/// <summary>전체 정지. Quick=true 면 Quick Stop 후 서보 오프, false 면 감속 정지(Halt).</summary>
public sealed record StopRequest(bool Quick = false);

public sealed record SerialOpenRequest(int? BaudRate = null);

/// <summary>Text 또는 Hex("01 03 00 00 00 0A C5 CD") 중 하나. Text 는 AppendNewLine 이면 NewLine 을 붙인다.</summary>
public sealed record SerialWriteRequest(string? Text = null, string? Hex = null, bool AppendNewLine = true);

/// <summary>별칭 변경.</summary>
public sealed record AliasRequest(string Name);

/// <summary>출력 비트 설정.</summary>
public sealed record OutputBitRequest(bool Value);

/// <summary>출력 워드 설정. Mask 가 있으면 그 비트만 바꾼다.</summary>
public sealed record OutputWordRequest(ulong Value, ulong? Mask = null);

/// <summary>(시뮬레이션) 입력 강제. null 이면 강제 해제.</summary>
public sealed record ForceInputRequest(bool? Value);

/// <summary>SDO 쓰기 요청. Size 바이트(1/2/4)로 잘려 전송된다.</summary>
public sealed record SdoWriteRequest(int Size, uint Value);

public sealed record CommandAccepted(int AxisId, string Command, string Message = "queued for next EtherCAT cycle");
