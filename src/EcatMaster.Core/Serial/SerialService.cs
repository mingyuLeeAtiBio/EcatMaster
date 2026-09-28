using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EcatMaster.Core.Serial;

public sealed record SerialPortStatus(string Name, string Device, int BaudRate, bool IsOpen, int BufferedLines, long RxLines, long TxWrites, string? LastError);

/// <summary>
/// 설정된 시리얼 포트들을 이름으로 관리한다. 포트마다 수신 스레드가 라인 단위로 읽어 큐에 쌓고,
/// <see cref="LineReceived"/> 이벤트로도 알린다(시리얼 명령 → 모션 명령 연결 지점).
/// </summary>
public sealed class SerialService : IHostedService, IDisposable
{
    private readonly ILogger<SerialService> _log;
    private readonly Dictionary<string, ManagedPort> _ports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>(포트 이름, 수신 라인). 수신 스레드에서 호출되므로 핸들러는 짧게.</summary>
    public event Action<string, string>? LineReceived;

    public SerialService(IOptions<SerialOptions> options, ILogger<SerialService> log)
    {
        _log = log;
        foreach (var p in options.Value.Ports)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;
            _ports[p.Name] = new ManagedPort(p, this);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var p in _ports.Values.Where(p => p.Options.AutoOpen))
        {
            try { p.Open(); _log.LogInformation("Serial '{Name}' opened on {Dev}", p.Options.Name, p.Options.Device); }
            catch (Exception ex) { _log.LogError(ex, "Serial '{Name}' auto-open failed", p.Options.Name); }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var p in _ports.Values) p.Close();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var p in _ports.Values) p.Dispose();
    }

    public static string[] AvailableDevices() => SerialPort.GetPortNames();

    public IReadOnlyList<SerialPortStatus> GetAll() => _ports.Values.Select(p => p.GetStatus()).ToList();

    public SerialPortStatus? GetStatus(string name) => _ports.TryGetValue(name, out var p) ? p.GetStatus() : null;

    public bool Exists(string name) => _ports.ContainsKey(name);

    public void Open(string name, int? baudRate = null) => Get(name).Open(baudRate);

    public void Close(string name) => Get(name).Close();

    public void WriteLine(string name, string text) => Get(name).Write(text + Get(name).Options.NewLine);

    public void Write(string name, string text) => Get(name).Write(text);

    public void WriteBytes(string name, byte[] data) => Get(name).WriteBytes(data);

    public IReadOnlyList<string> ReadLines(string name, int max = 100) => Get(name).Drain(max);

    private ManagedPort Get(string name) =>
        _ports.TryGetValue(name, out var p) ? p : throw new KeyNotFoundException($"serial port '{name}' is not configured");

    internal void RaiseLine(string name, string line) => LineReceived?.Invoke(name, line);

    // ------------------------------------------------------------------

    private sealed class ManagedPort : IDisposable
    {
        private readonly SerialService _owner;
        private readonly ConcurrentQueue<string> _rx = new();
        private SerialPort? _port;
        private Thread? _reader;
        private volatile bool _running;
        private long _rxLines;
        private long _txWrites;
        private volatile string? _lastError;

        public SerialPortOptions Options { get; }

        public ManagedPort(SerialPortOptions options, SerialService owner)
        {
            Options = options;
            _owner = owner;
        }

        public void Open(int? baudRate = null)
        {
            lock (this)
            {
                if (_port?.IsOpen == true) return;

                var port = new SerialPort(Options.Device, baudRate ?? Options.BaudRate,
                    Enum.Parse<Parity>(Options.Parity, true), Options.DataBits, Enum.Parse<StopBits>(Options.StopBits, true))
                {
                    ReadTimeout = Options.ReadTimeoutMs,
                    WriteTimeout = Options.WriteTimeoutMs,
                    NewLine = string.IsNullOrEmpty(Options.NewLine) ? "\n" : Options.NewLine,
                    Encoding = Encoding.ASCII,
                };
                port.Open();
                _port = port;
                _lastError = null;

                _running = true;
                _reader = new Thread(ReadLoop) { Name = $"serial-{Options.Name}", IsBackground = true };
                _reader.Start();
            }
        }

        public void Close()
        {
            lock (this)
            {
                _running = false;
                try { _port?.Close(); } catch { /* ignore */ }
                _reader?.Join(TimeSpan.FromSeconds(1));
                _port?.Dispose();
                _port = null;
            }
        }

        public void Write(string text)
        {
            var p = _port ?? throw new InvalidOperationException($"serial port '{Options.Name}' is not open");
            p.Write(text);
            Interlocked.Increment(ref _txWrites);
        }

        public void WriteBytes(byte[] data)
        {
            var p = _port ?? throw new InvalidOperationException($"serial port '{Options.Name}' is not open");
            p.Write(data, 0, data.Length);
            Interlocked.Increment(ref _txWrites);
        }

        public IReadOnlyList<string> Drain(int max)
        {
            var list = new List<string>();
            while (list.Count < max && _rx.TryDequeue(out var line)) list.Add(line);
            return list;
        }

        public SerialPortStatus GetStatus() => new(Options.Name, Options.Device, _port?.BaudRate ?? Options.BaudRate,
            _port?.IsOpen == true, _rx.Count, Interlocked.Read(ref _rxLines), Interlocked.Read(ref _txWrites), _lastError);

        private void ReadLoop()
        {
            var port = _port;
            while (_running && port is not null)
            {
                try
                {
                    // Linux 의 System.IO.Ports 는 DataReceived 이벤트가 불안정해 블로킹 읽기를 쓴다.
                    string line = port.ReadLine();
                    _rx.Enqueue(line);
                    Interlocked.Increment(ref _rxLines);
                    while (_rx.Count > Options.MaxBufferedLines) _rx.TryDequeue(out _);
                    _owner.RaiseLine(Options.Name, line);
                }
                catch (TimeoutException) { /* 정상: 주기적으로 _running 확인 */ }
                catch (Exception ex) when (_running)
                {
                    _lastError = ex.Message;
                    Thread.Sleep(100);
                }
                catch { break; }
            }
        }

        public void Dispose() => Close();
    }
}
