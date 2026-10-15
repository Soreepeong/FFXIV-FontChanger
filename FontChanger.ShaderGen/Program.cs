using System;
using System.Globalization;
using System.IO;
using System.Text;
using FontChanger.DalamudPlugin.Rendering.Shaders;

namespace FontChanger.ShaderGen;

/// <summary>
/// Writes FontEdgeSquares.hlsli, or compiles FontEdgePS.hlsl: puts the generated functions in place of its include line,
/// compiles the result (ps_5_0, main, optimization level 3), and writes the bytecode,
/// the completed source and its listing. The include is kept in the repository, so that a build can compile
/// FontEdgePS.hlsl with fxc alone; compiling fails if it differs from what would be written now, or if patching the
/// softness into the bytecode (PixelShaderSwap) doesn't give what compiling with it does.
/// </summary>
/// <remarks>
/// Usage: FontChanger.ShaderGen &lt;FontEdgePS.hlsl&gt; &lt;output .cso&gt;, or FontChanger.ShaderGen --write-include
/// &lt;FontEdgeSquares.hlsli&gt;. The completed source and listing go next to the output (.hlsl, .asm). Compile errors go
/// to stderr in the "file(line,col): error" form, and the exit code is 1.
/// FontChanger.ShaderGen --subpixel &lt;FontSubpixelPS.hlsl&gt; &lt;output .cso&gt; [--highlight] compiles the subpixel
/// text shader (with HIGHLIGHT defined), checking its settings' patching (SubpixelShaders) the same way.
/// </remarks>
internal static class Program
{
    private const string IncludeName = "FontEdgeSquares.hlsli";
    private const string IncludeLine = $"#include \"{IncludeName}\"";

    private static string IncludeText() =>
        "// SQUARE_REACH and DilateGatheredSquare for FontEdgePS.hlsl, written by FontChanger.ShaderGen --write-include; do\n"
        + "// not edit by hand.\n\n"
        + FontEdgeSquares.Generate() + "\n";

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--write-include")
        {
            File.WriteAllText(Path.GetFullPath(args[1]), IncludeText());
            return 0;
        }

        if (args.Length is 3 or 4 && args[0] == "--subpixel" && (args.Length == 3 || args[3] == "--highlight"))
            return CompileSubpixel(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args.Length == 4);

        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: FontChanger.ShaderGen <FontEdgePS.hlsl> <output .cso>");
            Console.Error.WriteLine("       FontChanger.ShaderGen --write-include <FontEdgeSquares.hlsli>");
            Console.Error.WriteLine("       FontChanger.ShaderGen --subpixel <FontSubpixelPS.hlsl> <output .cso> [--highlight]");
            return 2;
        }

        var templatePath = Path.GetFullPath(args[0]);
        var outputPath = Path.GetFullPath(args[1]);
        var template = File.ReadAllText(templatePath).Replace("\r\n", "\n");
        var at = template.IndexOf(IncludeLine, StringComparison.Ordinal);
        if (at < 0)
        {
            Console.Error.WriteLine($"{templatePath}: error: the line {IncludeLine} is missing.");
            return 1;
        }

        var includePath = Path.Combine(Path.GetDirectoryName(templatePath)!, IncludeName);
        var include = IncludeText();
        if (!File.Exists(includePath) || File.ReadAllText(includePath).Replace("\r\n", "\n") != include)
        {
            Console.Error.WriteLine($"{includePath}: error: out of date; run FontChanger.ShaderGen --write-include \"{includePath}\".");
            return 1;
        }

        var source = template[..at] + include + template[(at + IncludeLine.Length)..];
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var sourcePath = Path.ChangeExtension(outputPath, ".hlsl");
        File.WriteAllText(sourcePath, source);

        // Errors name the completed source, whose lines match the template's up to the marker.
        if (!D3DCompiler.Compile(Encoding.UTF8.GetBytes(source), sourcePath, "main", "ps_5_0", out var code, out var messages))
        {
            Console.Error.WriteLine(messages);
            return 1;
        }

        if (messages.Length > 0)
            Console.WriteLine(messages);
        if (CheckSoftnessPatch(source, sourcePath, code) is { } error)
        {
            Console.Error.WriteLine($"{templatePath}: error: {error}");
            return 1;
        }

        File.WriteAllBytes(outputPath, code);
        File.WriteAllText(Path.ChangeExtension(outputPath, ".asm"), D3DCompiler.Disassemble(code));
        return 0;
    }

    /// <summary>Compiles FontSubpixelPS.hlsl, and checks that patching its ClearTypeLevel, Contrast and Gamma gives what compiling with them does.</summary>
    private static int CompileSubpixel(string sourcePath, string outputPath, bool highlight)
    {
        const string levelDeclaration = "static const float ClearTypeLevel = 0.234567891f;";
        const string contrastDeclaration = "static const float Contrast = 0.345678912f;";
        const string gammaDeclaration = "static const float Gamma = 0.456789123f;";
        const float testLevel = 0.75f;
        const float testContrast = 1.25f;
        const float testGamma = 2.2f;

        var source = File.ReadAllText(sourcePath).Replace("\r\n", "\n");
        if (highlight)
            source = "#define HIGHLIGHT\n#line 1\n" + source;
        if (!D3DCompiler.Compile(Encoding.UTF8.GetBytes(source), sourcePath, "main", "ps_5_0", out var code, out var messages))
        {
            Console.Error.WriteLine(messages);
            return 1;
        }

        if (messages.Length > 0)
            Console.WriteLine(messages);
        if (!source.Contains(levelDeclaration, StringComparison.Ordinal) || !source.Contains(contrastDeclaration, StringComparison.Ordinal)
            || !source.Contains(gammaDeclaration, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"{sourcePath}: error: the lines {levelDeclaration}, {contrastDeclaration} and {gammaDeclaration} are needed.");
            return 1;
        }

        var patched = (byte[])code.Clone();
        if (!PixelShaderSwap.PatchConstants(
                patched,
                [
                    (SubpixelShaders.ClearTypeLevelSentinel, testLevel),
                    (SubpixelShaders.ContrastSentinel, testContrast),
                    (SubpixelShaders.GammaSentinel, testGamma),
                ]))
        {
            Console.Error.WriteLine($"{sourcePath}: error: ClearTypeLevel, Contrast and Gamma aren't each an immediate operand exactly once in the bytecode.");
            return 1;
        }

        var withTest = source
                       .Replace(levelDeclaration, $"static const float ClearTypeLevel = {testLevel.ToString("R", CultureInfo.InvariantCulture)}f;", StringComparison.Ordinal)
                       .Replace(contrastDeclaration, $"static const float Contrast = {testContrast.ToString("R", CultureInfo.InvariantCulture)}f;", StringComparison.Ordinal)
                       .Replace(gammaDeclaration, $"static const float Gamma = {testGamma.ToString("R", CultureInfo.InvariantCulture)}f;", StringComparison.Ordinal);
        if (!D3DCompiler.Compile(Encoding.UTF8.GetBytes(withTest), sourcePath, "main", "ps_5_0", out var expected, out messages))
        {
            Console.Error.WriteLine($"{sourcePath}: error: compiling with the test values failed: {messages}");
            return 1;
        }

        if (!patched.AsSpan().SequenceEqual(expected))
        {
            Console.Error.WriteLine($"{sourcePath}: error: patching the settings differs from compiling with them ({patched.Length} and {expected.Length} bytes).");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, code);
        File.WriteAllText(Path.ChangeExtension(outputPath, ".asm"), D3DCompiler.Disassemble(code));
        return 0;
    }

    /// <summary>
    /// Checks that the plugin's patching (PixelShaderSwap) works on this bytecode: the checksum is computed as the
    /// compiler does, and patching a softness gives what compiling with it written in gives, byte for byte.
    /// </summary>
    private static string? CheckSoftnessPatch(string source, string sourcePath, byte[] code)
    {
        const string declaration = "static const float Softness = 0.123456789f;";
        const float test = 1.75f;

        var checksum = new byte[16];
        PixelShaderSwap.ComputeChecksum(code, checksum);
        if (!checksum.AsSpan().SequenceEqual(code.AsSpan(4, 16)))
            return "the computed DXBC checksum differs from the compiler's.";

        if (!source.Contains(declaration, StringComparison.Ordinal))
            return $"the line {declaration} is missing.";
        var patched = (byte[])code.Clone();
        if (!PixelShaderSwap.PatchConstants(patched, (EdgeShader.Sentinel, test)))
            return "Softness isn't an immediate operand exactly once in the bytecode.";

        var withTest = source.Replace(declaration, $"static const float Softness = {test.ToString("R", CultureInfo.InvariantCulture)}f;", StringComparison.Ordinal);
        if (!D3DCompiler.Compile(Encoding.UTF8.GetBytes(withTest), sourcePath, "main", "ps_5_0", out var expected, out var messages))
            return $"compiling with Softness = {test} failed: {messages}";
        return patched.AsSpan().SequenceEqual(expected)
            ? null
            : $"patching Softness to {test} differs from compiling with it ({patched.Length} and {expected.Length} bytes).";
    }
}
