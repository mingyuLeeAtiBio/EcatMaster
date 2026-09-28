namespace EcatMaster.Core.Serial;

/// <summary>appsettings.json 의 "Serial" 섹션.</summary>
public sealed class SerialOptions
{
    public const string Section = "Serial";

    public List<SerialPortOptions> Ports { get; set; } = new();
}

public sealed class SerialPortOptions
{
    /// <summary>API 에서 부르는 논리 이름 (예: "plc", "barcode").</summary>
    public string Name { get; set; } = "";

    /// <summary>Linux: /dev/ttyUSB0, /dev/ttyS0 / Windows: COM3</summary>
    public string Device { get; set; } = "";

    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;

    /// <summary>None | Odd | Even | Mark | Space</summary>
    public string Parity { get; set; } = "None";

    /// <summary>One | Two | OnePointFive</summary>
    public string StopBits { get; set; } = "One";

    /// <summary>수신 라인 구분자. 바이너리 프로토콜이면 빈 문자열로 두고 ReadBytes 를 쓴다.</summary>
    public string NewLine { get; set; } = "\r\n";

    public int ReadTimeoutMs { get; set; } = 200;
    public int WriteTimeoutMs { get; set; } = 500;

    /// <summary>앱 시작 시 자동으로 열기.</summary>
    public bool AutoOpen { get; set; } = false;

    /// <summary>수신 버퍼에 보관할 최대 라인 수(초과 시 오래된 것 삭제).</summary>
    public int MaxBufferedLines { get; set; } = 1000;
}
