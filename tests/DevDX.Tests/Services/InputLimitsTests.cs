using DevDX.Services;
using Xunit;

namespace DevDX.Tests.Services;

/// <summary>
/// Design doc §22: "&gt; 50 MB — refused with a clear, specific message — never an OOM, never a
/// hang", and §21's "parse-bomb and allocation guards … checked before reading a file".
/// <para>
/// Before <see cref="InputLimits"/> existed, every tool that opened a file called
/// <c>File.ReadAllTextAsync</c> straight on whatever the picker returned, so the contract was
/// documented and nowhere implemented. These tests hold the implementation to the two halves of it
/// that matter: the decision is made from the file's <em>metadata</em>, before any read, and the
/// refusal comes back as a sentence rather than an exception.
/// </para>
/// </summary>
public class InputLimitsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "devdx-inputlimits-" + Guid.NewGuid().ToString("N"));

    public InputLimitsTests()
    {
        Directory.CreateDirectory(_directory);
        // So the refusal messages resolve to real sentences rather than bare keys.
        Loc.Initialize("en");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, long length)
    {
        var path = Path.Combine(_directory, name);
        // Sparse-ish: the length is set without writing the bytes, which is the point — the guard
        // must decide from the file's size, so a test for it must never need to materialize one.
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(length);
        return path;
    }

    [Fact]
    public async Task FileOverTheCeiling_IsRefusedWithAMessageNamingIt()
    {
        // A small explicit ceiling rather than the real 50 MB one: the behaviour under test is
        // "decided from the file's length before reading", and proving that does not require
        // producing a 50 MB file on a build agent. The real constant is asserted separately below.
        const long limit = 4 * 1024 * 1024;
        var path = Write("huge.json", limit + 1);

        var result = await InputLimits.ReadTextAsync(path, limit);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Error);
        // "Clear, specific" per §22: the message names the ceiling rather than saying "too large".
        Assert.Contains(InputLimits.Describe(limit), result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Text);
    }

    [Fact]
    public void TheShippedCeilingsAreTheOnesTheDesignDocumentStates()
    {
        // §22's table refuses past 50 MB. If this number ever moves, it moves in the document too.
        Assert.Equal(50L * 1024 * 1024, InputLimits.MaxTextBytes);
    }

    [Fact]
    public async Task FileExactlyAtTheCeiling_IsAccepted()
    {
        // The boundary is inclusive, so a file of exactly the limit is not refused.
        Assert.True(InputLimits.IsWithin(InputLimits.MaxTextBytes, InputLimits.MaxTextBytes));
        Assert.False(InputLimits.IsWithin(InputLimits.MaxTextBytes + 1, InputLimits.MaxTextBytes));

        var path = Write("exact.json", 1024);
        var result = await InputLimits.ReadTextAsync(path, 1024);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task OrdinaryFile_ReadsBackExactly()
    {
        var path = Path.Combine(_directory, "ordinary.json");
        const string content = """{"name":"Ada","note":"héllo — ünicode"}""";
        await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);

        var result = await InputLimits.ReadTextAsync(path);

        Assert.True(result.Ok);
        Assert.Equal(content, result.Text);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task MissingFile_IsReportedRatherThanThrown()
    {
        var result = await InputLimits.ReadTextAsync(Path.Combine(_directory, "not-here.json"));

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public async Task BinaryReadHasItsOwnHigherCeiling_AndStillRefusesPastIt()
    {
        // Higher than the text ceiling because nothing tokenizes or lays these bytes out — they are
        // hashed or encoded once — but bounded all the same.
        Assert.True(InputLimits.MaxBinaryBytes > InputLimits.MaxTextBytes);

        const long limit = 1024 * 1024;
        var path = Write("blob.bin", limit + 1);

        var result = await InputLimits.ReadBytesAsync(path, limit);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Error);
        Assert.Empty(result.Bytes);
    }

    [Fact]
    public async Task IconCeilingIsMuchSmallerThanTheTextOne()
    {
        // An icon is drawn at 44 px. The ceiling exists because this is the one place DevDX hands
        // a user-supplied file to an image decoder.
        Assert.True(InputLimits.MaxIconBytes < InputLimits.MaxTextBytes);

        const long limit = 64 * 1024;
        var path = Write("not-an-icon.png", limit + 1);
        var result = await InputLimits.ReadBytesAsync(path, limit);

        Assert.False(result.Ok);
    }

    [Fact]
    public void ConfigCeilingIsSmallEnoughToRejectSomethingThatIsNotAConfig()
    {
        Assert.True(InputLimits.MaxConfigBytes < InputLimits.MaxTextBytes);
    }

    [Theory]
    [InlineData(50L * 1024 * 1024, "50 MB")]
    [InlineData(256L * 1024 * 1024, "256 MB")]
    [InlineData(16L * 1024 * 1024, "16 MB")]
    [InlineData(2L * 1024 * 1024 * 1024, "2 GB")]
    public void CeilingIsDescribedInTheUnitAUserThinksIn(long bytes, string expected)
    {
        Assert.Equal(expected, InputLimits.Describe(bytes));
    }

    [Fact]
    public void ImportingAnOversizedFileAsAConfig_IsRefused()
    {
        // DockStore.ImportFrom accepts any path the picker returns, so it carries the same guard.
        var path = Write("not-a-config.bin", InputLimits.MaxConfigBytes + 1);

        Assert.Null(DockStore.ImportFrom(path));
    }
}
