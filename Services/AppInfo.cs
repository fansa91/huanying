using System;
using System.Reflection;

namespace Huanying.Services;

/// <summary>
/// 应用自身信息。版本号不写死，取自编译时写入的程序集元数据。
/// CI 会用 tag 覆盖：推 <c>v0.1.1</c> → <c>dotnet publish -p:Version=0.1.1</c>，
/// 于是关于页显示的也就是 tag 的版本号。
/// </summary>
internal static class AppInfo
{
    /// <summary>版本号，形如 <c>0.1.1</c>。</summary>
    public static string Version { get; } = Resolve();

    /// <summary>关于页展示用，形如 <c>0.1.1 (WinUI 3)</c>。</summary>
    public static string DisplayVersion => $"{Version} (WinUI 3)";

    private static string Resolve()
    {
        var assembly = typeof(AppInfo).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // 去掉源码版本后缀（形如 0.1.1+3f2a1b9）
            var plus = informational.IndexOf('+');
            var clean = (plus >= 0 ? informational[..plus] : informational).Trim();
            if (clean.Length > 0) return clean;
        }

        var file = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(file)) return file.Trim();

        var v = assembly.GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
