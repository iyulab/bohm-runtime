using System.Runtime.InteropServices;
using System.Text;

namespace Bohm.Runtime.TableImports;

/// <summary>
/// The legacy code page a text file that is not UTF-8 is read in on this computer when the person has
/// not chosen one — the one a spreadsheet here saves «CSV» in: Windows' ANSI code page, and only when it
/// is a double-byte one (CP949 on Korean Windows). A single-byte code page (Windows-1252) reads any
/// bytes at all — a Korean bank's file would come out garbled, not refused — so it is never assumed;
/// the person chooses it. Elsewhere there is none.
/// </summary>
public static partial class LegacyText
{
    private static readonly Lazy<Encoding?> ThisComputer = new(() => OperatingSystem.IsWindows() && TableFile.StrictLegacy((int)GetACP()) is { IsSingleByte: false } doubleByte ? doubleByte : null);

    /// <summary>The strict double-byte legacy encoding of this computer, or <see langword="null"/> when it has none (not Windows, a single-byte or UTF-8 code page).</summary>
    public static Encoding? OfThisComputer() => ThisComputer.Value;

    [LibraryImport("kernel32.dll")]
    private static partial uint GetACP();
}
