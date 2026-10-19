using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfflineTranslator.Core.Models;

namespace OfflineTranslator.Core;

/// <summary>
/// 扫描程序同级 models 目录里的 Hy-MT2 GGUF。
/// 开发时若输出目录旁边没有模型，会向上找到仓库里的 models。
/// </summary>
public static class ModelScanner
{
    public static List<ModelInfo> Scan(string modelsDirectory)
    {
        var result = new List<ModelInfo>();
        if (string.IsNullOrWhiteSpace(modelsDirectory) || !Directory.Exists(modelsDirectory))
            return result;

        foreach (var file in Directory.EnumerateFiles(modelsDirectory, "*.gguf", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                result.Add(new ModelInfo(info.Name, info.FullName, info.Length));
            }
            catch { }
        }

        // 自检用的假目录。已废弃的 opus-mt 目录不列出。
        foreach (var dir in Directory.EnumerateDirectories(modelsDirectory))
        {
            try
            {
                var dirName = Path.GetFileName(dir);
                if (dirName.StartsWith("opus-mt-", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.EnumerateFiles(dir, "*.gguf", SearchOption.AllDirectories).Any()) continue;
                var hasOnnx = Directory.EnumerateFiles(dir, "*.onnx", SearchOption.AllDirectories).Any();
                if (!hasOnnx) continue;
                result.Add(new ModelInfo(dirName, dir, DirectorySize(dir)));
            }
            catch { }
        }

        result.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>目录里的第一个 GGUF；目录本身是文件时直接返回。</summary>
    public static string ResolveModelFile(string path)
    {
        if (File.Exists(path)) return path;
        if (Directory.Exists(path))
        {
            var gguf = Directory.EnumerateFiles(path, "*.gguf", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (gguf is not null) return gguf;
        }

        throw new FileNotFoundException(
            "未找到 Hy-MT2 模型。请把 .gguf 放在程序同级 models\\Hy-MT2 目录。", path);
    }

    private static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    public static string GetDefaultModelsDirectory(string? appBaseDirectory = null, string modelsSubFolder = "models")
    {
        var start = appBaseDirectory ?? GetAppBaseDirectory();
        var beside = Path.Combine(start, modelsSubFolder);
        if (HasGguf(beside)) return beside;

        var dir = new DirectoryInfo(start);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, modelsSubFolder);
            if (HasGguf(candidate)) return candidate;
            dir = dir.Parent;
        }

        return beside;
    }

    private static bool HasGguf(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                && Directory.EnumerateFiles(directory, "*.gguf", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    public static string GetAppBaseDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir)) return Directory.GetCurrentDirectory();
        return baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
