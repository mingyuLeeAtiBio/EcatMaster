using EcatMaster.Api;
using EcatMaster.Api.Endpoints;
using EcatMaster.Core.EtherCat;
using EcatMaster.Core.Serial;

var builder = WebApplication.CreateBuilder(args);

// ---- 설정 바인딩 ----
builder.Services.Configure<MasterOptions>(builder.Configuration.GetSection(MasterOptions.Section));
builder.Services.Configure<SerialOptions>(builder.Configuration.GetSection(SerialOptions.Section));

// ---- EtherCAT 마스터 (Soem | Simulated) ----
var mode = builder.Configuration[$"{MasterOptions.Section}:Mode"] ?? "Simulated";
if (string.Equals(mode, "Soem", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IEtherCatMaster, SoemMaster>();
else
    builder.Services.AddSingleton<IEtherCatMaster, SimulatedMaster>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<IEtherCatMaster>());

// ---- 왕복 테스트 실행기 ----
builder.Services.AddSingleton<CycleTestRunner>();

// ---- 시간 기반 연속 운전 (내구 시험) ----
builder.Services.AddSingleton<EnduranceTestRunner>();

// ---- 시리얼 ----
builder.Services.AddSingleton<SerialService>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SerialService>());

// ---- 시리얼 수신 → 모션 명령 브리지 (외부 장비가 시리얼로 명령을 줄 때 여기서 해석) ----
builder.Services.AddHostedService<SerialCommandBridge>();

// ---- OpenAPI ----
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();                                // GET /openapi/v1.json
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi/v1.json", "EcatMaster API v1");
    o.RoutePrefix = "swagger";                   // http://host:5000/swagger
});

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.MapSystemEndpoints();
app.MapAxisEndpoints();
app.MapIoEndpoints();
app.MapSerialEndpoints();

app.Run();
