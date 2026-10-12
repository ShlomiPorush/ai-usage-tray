using System.Runtime.InteropServices;
using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

/// <summary>
/// Which Codex binary the app starts, against a fake file system. Paths are
/// made up; nothing on this machine is looked at.
/// </summary>
public sealed class CodexExecutableResolverTests
{
    private const string Local = @"C:\Users\sam\AppData\Local";
    private const string Roaming = @"C:\Users\sam\AppData\Roaming";
    private const string Standalone = Local + @"\Programs\OpenAI\Codex\bin\codex.exe";
    private const string NpmPrefix = Roaming + @"\npm";
    private const string NpmShim = NpmPrefix + @"\codex.cmd";
    private const string NpmNative = NpmPrefix +
        @"\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.JS";

    private static string Resolve(
        IEnumerable<string> files,
        string? path = NpmPrefix + @";C:\Windows\System32",
        string? pathExt = DefaultPathExt,
        Architecture architecture = Architecture.X64,
        string executable = "codex")
    {
        var existing = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return CodexExecutableResolver.Resolve(
            executable,
            new CodexResolverEnvironment(true, Local, Roaming, path, pathExt, architecture, existing.Contains));
    }

    [Fact]
    public void Standalone_install_wins_when_present()
    {
        Assert.Equal(Standalone, Resolve([Standalone]));
    }

    [Fact]
    public void Npm_only_install_starts_the_native_binary_not_the_shim()
    {
        Assert.Equal(NpmNative, Resolve([NpmShim, NpmPrefix + @"\codex.ps1", NpmPrefix + @"\codex", NpmNative]));
    }

    [Fact]
    public void Both_installed_uses_the_standalone_one()
    {
        Assert.Equal(Standalone, Resolve([Standalone, NpmShim, NpmNative]));
    }

    [Fact]
    public void Neither_installed_keeps_the_bare_name()
    {
        Assert.Equal("codex", Resolve([]));
    }

    [Fact]
    public void An_npm_shim_without_its_native_binary_is_used_as_is()
    {
        Assert.Equal(NpmShim, Resolve([NpmShim]));
    }

    [Fact]
    public void A_shim_in_a_custom_npm_prefix_on_path_resolves_to_its_native_binary()
    {
        const string prefix = @"D:\tools\node";
        var native = prefix +
            @"\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";

        Assert.Equal(native, Resolve([prefix + @"\codex.cmd", native], path: @"C:\Windows;" + prefix));
    }

    [Fact]
    public void A_codex_exe_on_path_is_used_directly_in_path_order()
    {
        Assert.Equal(
            @"D:\first\codex.exe",
            Resolve([@"D:\first\codex.exe", @"D:\second\codex.exe"], path: @"D:\first;D:\second"));
    }

    [Fact]
    public void Pathext_order_decides_within_one_folder()
    {
        string[] files = [@"D:\bin\codex.cmd", @"D:\bin\codex.exe"];

        Assert.Equal(@"D:\bin\codex.cmd", Resolve(files, path: @"D:\bin", pathExt: ".CMD;.EXE"));
        Assert.Equal(@"D:\bin\codex.exe", Resolve(files, path: @"D:\bin", pathExt: ".EXE;.CMD"));
    }

    [Fact]
    public void Relative_and_empty_path_entries_are_ignored()
    {
        Assert.Equal("codex", Resolve([@"codex.exe", @".\codex.exe"], path: @";.;relative\dir;"));
    }

    [Fact]
    public void Missing_pathext_falls_back_to_the_windows_defaults()
    {
        Assert.Equal(@"D:\bin\codex.exe", Resolve([@"D:\bin\codex.exe"], path: @"D:\bin", pathExt: null));
    }

    [Fact]
    public void Older_single_package_npm_layout_is_found()
    {
        var old = NpmPrefix + @"\node_modules\@openai\codex\vendor\x86_64-pc-windows-msvc\codex\codex.exe";

        Assert.Equal(old, Resolve([NpmShim, old]));
    }

    [Fact]
    public void Arm64_prefers_the_arm64_package_and_falls_back_to_x64()
    {
        var arm = NpmPrefix +
            @"\node_modules\@openai\codex\node_modules\@openai\codex-win32-arm64\vendor\aarch64-pc-windows-msvc\bin\codex.exe";

        Assert.Equal(arm, Resolve([NpmShim, arm, NpmNative], architecture: Architecture.Arm64));
        Assert.Equal(NpmNative, Resolve([NpmShim, NpmNative], architecture: Architecture.Arm64));
    }

    [Fact]
    public void An_explicit_executable_is_never_rewritten()
    {
        Assert.Equal(@"D:\custom\codex.exe", Resolve([Standalone], executable: @"D:\custom\codex.exe"));
    }

    [Fact]
    public void Non_windows_keeps_the_bare_name()
    {
        var existing = new HashSet<string>([Standalone], StringComparer.OrdinalIgnoreCase);
        var result = CodexExecutableResolver.Resolve(
            "codex",
            new CodexResolverEnvironment(false, Local, Roaming, NpmPrefix, DefaultPathExt, Architecture.X64, existing.Contains));

        Assert.Equal("codex", result);
    }
}
