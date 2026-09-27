using System.IO;

namespace XCL2.App.Services;

/// <summary>从当前构建的程序集读取帮助文档，查看和导出均不依赖外部 MD 文件或网络。</summary>
public static class EmbeddedDocumentationService
{
    public const string SolutionsFileName = "PAPERDOLL_FIX.md";
    public const string ApiFileName = "API_REFERENCE.md";

    public static string GetTitle(string fileName) => fileName switch
    {
        SolutionsFileName => "解决方法与修复说明",
        ApiFileName => "启动器 API 接入说明",
        _ => throw new ArgumentException("未知的帮助文档。", nameof(fileName))
    };

    private static Stream OpenResource(string fileName)
    {
        _ = GetTitle(fileName);
        return typeof(EmbeddedDocumentationService).Assembly
            .GetManifestResourceStream("XCL2.App.Docs." + fileName)
            ?? throw new FileNotFoundException("未找到内置帮助文档：" + fileName);
    }

    public static string Read(string fileName)
    {
        using var stream = OpenResource(fileName);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static void Export(string fileName, string destination)
    {
        using var stream = OpenResource(fileName);
        using var output = File.Create(destination);
        stream.CopyTo(output);
    }
}
