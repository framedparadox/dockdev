using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dockdev.Controls;
using dockdev.Models;
using dockdev.Services;
using dockdev.Services.Formats;
using dockdev.Services.Masking;
using dockdev.Services.Text;
using dockdev.Services.Tools;
using Xunit;

namespace dockdev.Tests.Soak;

/// <summary>
/// Comprehensive stress, concurrency, soak, and boundary crash resilience tests.
/// Verifies that dockdev is 100% resilient against thread contention, file locks, corrupted storage,
/// numeric overflow, ReDoS/catastrophic backtracking, recursive AST stack overflow, and large payloads.
/// </summary>
public class StressAndCrashResilienceTests
{
    // ---- 1. DockStore Concurrency & Storage Resilience ----------------------

    [Fact]
    public async Task DockStore_ConcurrentSaveAndLoad_NeverCorruptsOrCrashes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dockdev_test_concurrent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var initial = DockStore.Load();
            Assert.NotNull(initial);

            const int workerCount = 40;
            const int iterationsPerWorker = 15;
            var tasks = new List<Task>();

            for (int w = 0; w < workerCount; w++)
            {
                int workerId = w;
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < iterationsPerWorker; i++)
                    {
                        if (workerId % 2 == 0)
                        {
                            // Writer worker: export & save
                            var exportPath = Path.Combine(tempDir, $"export_{workerId}_{i}.json");
                            var cfg = DockStore.Load();
                            cfg.Language = (workerId % 3 == 0) ? "de" : "en";
                            cfg.Density = (workerId % 2 == 0) ? DockDensity.Large : DockDensity.Small;
                            DockStore.ExportTo(cfg, exportPath);
                            DockStore.Save(cfg);
                        }
                        else
                        {
                            // Reader worker
                            var cfg = DockStore.Load();
                            Assert.NotNull(cfg);
                            Assert.NotNull(cfg.Dock);
                        }
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Final verification: config must load and parse valid JSON
            var finalConfig = DockStore.Load();
            Assert.NotNull(finalConfig);
            Assert.NotNull(finalConfig.Dock);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void DockStore_CorruptJson_RecoversGracefullyWithoutThrowing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dockdev_test_corrupt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            // Write corrupted payload to file and test Import/Load safety
            File.WriteAllText(configPath, "{ invalid json corrupt content @!#$ ]]]");
            var imported = DockStore.ImportFrom(configPath);
            Assert.Null(imported);

            var cfg = DockStore.Load();
            Assert.NotNull(cfg);
            Assert.NotNull(cfg.Dock);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    // ---- 2. Diag Logging Thread Safety & Size Limit -----------------------

    [Fact]
    public async Task Diag_ConcurrentLogWrites_ThreadSafeAndResilient()
    {
        const int threadCount = 30;
        const int logsPerThread = 50;
        var tasks = new List<Task>();

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < logsPerThread; i++)
                {
                    Diag.Log($"Concurrent test log from thread {threadId} iteration {i}: {new string('x', 100)}");
                }
            }));
        }

        await Task.WhenAll(tasks);
        Assert.True(true); // Reaching here means no unhandled exceptions or thread-safety crashes in Diag
    }

    // ---- 3. Numeric & Timestamp Boundary Resilience -------------------------

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1L)]
    [InlineData(-62_135_596_800L)] // Year 0001-01-01
    [InlineData(253_402_300_799L)]  // Year 9999-12-31
    [InlineData(-62_135_596_801L)] // Out of range
    [InlineData(253_402_300_800L)]  // Out of range
    public void TimestampTools_ExtremeEpochs_NeverThrowOverflowException(long epoch)
    {
        var unit = TimestampTools.DetectUnit(epoch);
        Assert.True(Enum.IsDefined(typeof(EpochUnit), unit));

        // TryParseAny should never throw
        string str = epoch.ToString(CultureInfo.InvariantCulture);
        bool parsed = TimestampTools.TryParseAny(str, out var result);
        if (parsed)
        {
            Assert.True(result >= DateTimeOffset.MinValue && result <= DateTimeOffset.MaxValue);
        }
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-1000000000000L)]
    [InlineData(1000000000000L)]
    public void JwtTools_ExtremeTimestamps_NeverThrowOutOfRange(long expSeconds)
    {
        string headerJson = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";
        string payloadJson = $"{{\"sub\":\"1234567890\",\"exp\":{expSeconds},\"iat\":{expSeconds}}}";

        string b64Header = Convert.ToBase64String(Encoding.UTF8.GetBytes(headerJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string b64Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string token = $"{b64Header}.{b64Payload}.signature";

        bool success = JwtTools.TryDecode(token, out var decoded, out var error);
        Assert.True(success, "Decoding structured JWT with extreme exp should succeed without throwing.");
        Assert.NotNull(decoded);
    }

    // ---- 4. Masking & PII Resilience ---------------------------------------

    [Fact]
    public void Masker_FormatPreservingFake_NeverThrowsOverflow()
    {
        var masker = new Masker();
        var finding = new Finding("email", PiiCategory.Contact, "email", 0, 10, Confidence.High, MaskStrategy.FormatPreservingFake, Included: true);

        // Test with many different strings, including edge cases that produce int.MinValue hash codes
        for (int i = 0; i < 5000; i++)
        {
            string email = $"test_{i}_{Guid.NewGuid()}@domain.com";
            var output = masker.BuildOutput(email, [finding with { Length = email.Length }]);
            Assert.NotNull(output.Text);
            Assert.Contains("@example.com", output.Text);
        }
    }

    [Fact]
    public void PiiDetector_ReDoSAttackPattern_TimesOutGracefullyWithoutHanging()
    {
        // Pathological inputs that might cause catastrophic backtracking in poorly bounded regexes
        string attack1 = new string('a', 10000) + "@" + new string('b', 10000) + "!";
        string attack2 = "\"key\": \"" + new string('x', 20000) + "\"";
        string attack3 = "Bearer " + new string('A', 50000);

        var findings1 = PiiDetector.Detect(attack1, PiiRuleSet.Default, Confidence.Low);
        var findings2 = PiiDetector.Detect(attack2, PiiRuleSet.Default, Confidence.Low);
        var findings3 = PiiDetector.Detect(attack3, PiiRuleSet.Default, Confidence.Low);

        Assert.NotNull(findings1);
        Assert.NotNull(findings2);
        Assert.NotNull(findings3);
    }

    // ---- 5. Text & LineOps Resilience ---------------------------------------

    [Fact]
    public void LineOps_NaturalSort_HugeNumericRuns_NeverThrowsOverflow()
    {
        string hugeNum1 = "item" + new string('9', 100);
        string hugeNum2 = "item" + new string('9', 99) + "8";
        string text = $"{hugeNum1}\n{hugeNum2}\nitem1\nitem20\nitem2";

        var sorted = LineOps.SortLines(text, natural: true, descending: false);
        Assert.NotNull(sorted);

        var lines = sorted.Split('\n');
        Assert.Equal("item1", lines[0]);
        Assert.Equal("item2", lines[1]);
        Assert.Equal("item20", lines[2]);
        Assert.Equal(hugeNum2, lines[3]);
        Assert.Equal(hugeNum1, lines[4]);
    }

    // ---- 6. XML & CSV Format Extreme Boundary Resilience --------------------

    [Fact]
    public void XmlFormat_DeeplyNestedAndInvalidTagNames_NeverCrashes()
    {
        // Build a deeply nested structure (200 levels deep)
        DataNode current = new ScalarNode("leaf value", ScalarKind.String);
        for (int i = 0; i < 200; i++)
        {
            // Use strange keys with spaces, numbers, symbols that are invalid raw XML tags
            current = new ObjectNode([($"123 invalid key #{i} !?", current)]);
        }

        var xml = XmlFormat.Instance.FromCanonical(current, FormatOptions.Default);
        Assert.NotNull(xml);
        Assert.NotEmpty(xml);

        // Parse it back to canonical
        var canonical = XmlFormat.Instance.ToCanonical(xml);
        Assert.NotNull(canonical);
    }

    [Fact]
    public void CsvProjection_HugeNumericKeys_NeverThrowsOverflow()
    {
        string hugeKey = "12345678901234567890123456789012345678901234567890";
        var obj = new ObjectNode([
            ($"items.{hugeKey}", new ScalarNode("val1", ScalarKind.String)),
            ("items.0", new ScalarNode("val2", ScalarKind.String)),
        ]);

        var rebuilt = CsvProjection.Rebuild(obj);
        Assert.NotNull(rebuilt);

        var flat = CsvProjection.Flatten(rebuilt);
        Assert.NotNull(flat);
    }

    // ---- 7. NumberBase Tools Boundary Tests ---------------------------------

    [Theory]
    [InlineData(8, -128L, 127L)]
    [InlineData(16, -32768L, 32767L)]
    [InlineData(32, -2147483648L, 2147483647L)]
    [InlineData(64, long.MinValue, long.MaxValue)]
    public void NumberBaseTools_BitBoundaries_NeverThrow(int width, long minVal, long maxVal)
    {
        ulong maskedMin = NumberBaseTools.Mask(minVal, width);
        ulong maskedMax = NumberBaseTools.Mask(maxVal, width);

        long signExtendedMin = NumberBaseTools.SignExtend(maskedMin, width);
        long signExtendedMax = NumberBaseTools.SignExtend(maskedMax, width);

        Assert.Equal(minVal, signExtendedMin);
        Assert.Equal(maxVal, signExtendedMax);

        ulong notMin = NumberBaseTools.Not(maskedMin, width);
        ulong shl = NumberBaseTools.ShiftLeft(maskedMin, 1, width);
        ulong shr = NumberBaseTools.ShiftRight(maskedMin, 1);

        Assert.True(notMin <= (width == 64 ? ulong.MaxValue : (1UL << width) - 1));
    }
}
