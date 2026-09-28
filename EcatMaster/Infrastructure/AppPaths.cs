using System.IO;

namespace EcatMaster.Infrastructure;

/// <summary>
/// 사용자 데이터 폴더 (%LOCALAPPDATA%\EcatMaster): settings.json, devices.json, logs\.
/// 프로젝트 이름이 DirectControl → EcatMaster 로 바뀌었으므로, 새 폴더가 없고 옛 폴더가 있으면 첫 실행 때 내용을 복사해 온다(옛 폴더는 그대로 둠).
/// </summary>
public static class AppPaths
{
    public const string AppName = "EcatMaster";
    private const string LegacyAppName = "DirectControl";

    private static readonly Lazy<string> RootLazy = new(() =>
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string root = Path.Combine(local, AppName);
        string legacy = Path.Combine(local, LegacyAppName);
        try
        {
            if (!Directory.Exists(root) && Directory.Exists(legacy))
            {
                CopyDirectory(legacy, root);
                File.WriteAllText(Path.Combine(root, "MIGRATED_FROM_DirectControl.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} — {legacy} 의 설정·로그를 복사했습니다. 옛 폴더는 삭제해도 됩니다.");
            }
            Directory.CreateDirectory(root);
        }
        catch { /* 폴더를 못 만들면 각 파일 저장 시점에서 실패가 드러난다 */ }
        return root;
    });

    /// <summary>%LOCALAPPDATA%\EcatMaster</summary>
    public static string Root => RootLazy.Value;
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string DevicesFile => Path.Combine(Root, "devices.json");
    public static string LogDir => Path.Combine(Root, "logs");

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
