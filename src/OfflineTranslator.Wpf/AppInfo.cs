using System.Reflection;

namespace OfflineTranslator.Wpf;

internal static class AppInfo
{
    public const string Name = "离线翻译助手";

    public static string Version { get; } = ReadVersion();

    public static string Title => Name + " " + Version;

    private static string ReadVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return "1.0.0";
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }
}
