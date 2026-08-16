using System.IO;
using System.Text;
using EveDeck.Services;
using Xunit;

namespace EveDeck.Tests;

// Regression guard for a bug that silently disabled the entire Game Alerts feature.
//
// GameLogWatcherService was written by copying ChatLogWatcherService, including its
// `new StreamReader(stream, Encoding.Unicode)`. But EVE does NOT write the two log families the same
// way: Chatlogs\*.txt are UTF-16LE with a BOM, while Gamelogs\*.txt are plain UTF-8 with no BOM.
// Verified across a real archive spanning 2025-03 to 2026-08, 12k+ files.
//
// Decoding a UTF-8 gamelog as UTF-16 folds each pair of ASCII bytes into one garbage character, so
// no decoded line ever starts with '[' -- and ReadNewLines skips every line that does not. The result
// is a feature that builds, runs, logs no error, and matches nothing, forever.
public class GamelogEncodingTests
{
    private const string SampleLine =
        "[ 2026.08.16 12:00:00 ] (combat) <color=0xff00ffff><b>487</b> <color=0x77ffffff><font size=10>to</font> <b>Target</b>";

    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = GameLogWatcherService.OpenGamelogReader(stream);
        return reader.ReadToEnd();
    }

    // The actual on-disk format. This is the case that was broken.
    [Fact]
    public void Utf8WithoutBomDecodesIntactSoTheLineStillStartsWithBracket()
    {
        var decoded = Decode(new UTF8Encoding(false).GetBytes(SampleLine));

        Assert.Equal(SampleLine, decoded);
        Assert.StartsWith("[", decoded.TrimStart(), StringComparison.Ordinal);
    }

    // Proof of the failure mode, so the reason for the fix cannot be misread later: the OLD encoding
    // mangles the same bytes badly enough that the leading-bracket guard rejects the line.
    [Fact]
    public void DecodingTheSameBytesAsUtf16ProducesGarbage()
    {
        var utf8Bytes = new UTF8Encoding(false).GetBytes(SampleLine);
        var misdecoded = Encoding.Unicode.GetString(utf8Bytes);

        Assert.NotEqual(SampleLine, misdecoded);
        Assert.False(misdecoded.TrimStart().StartsWith('['));
    }

    // BOM detection stays on, so a future format change (or a hand-saved file) still decodes.
    [Fact]
    public void Utf16WithBomIsStillHonouredViaBomDetection()
    {
        var bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(SampleLine))
            .ToArray();

        Assert.Equal(SampleLine, Decode(bytes));
    }

    [Fact]
    public void Utf8WithBomIsHonoured()
    {
        var bytes = new UTF8Encoding(true).GetPreamble()
            .Concat(new UTF8Encoding(false).GetBytes(SampleLine))
            .ToArray();

        Assert.Equal(SampleLine, Decode(bytes));
    }

    // Non-ASCII matters: EVE ship and module names carry accented characters, and a UTF-8 file read
    // as anything narrower would corrupt exactly those.
    [Fact]
    public void NonAsciiSurvivesTheRoundTrip()
    {
        const string accented = "[ 2026.08.16 12:00:00 ] (combat) <b>120</b> from Rödsvart Frigate";
        Assert.Equal(accented, Decode(new UTF8Encoding(false).GetBytes(accented)));
    }
}
