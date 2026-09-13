using System.Globalization;
using System.Text.Json;
using VramMonitor.Core.Analysis;
using VramMonitor.Core.Configuration;
using VramMonitor.Core.Export;
using VramMonitor.Core.Model;
using VramMonitor.Core.Tests.Support;
using Xunit;
using static VramMonitor.Core.Tests.Support.Build;

namespace VramMonitor.Core.Tests.Export;

public sealed class ExportTests
{
    /// <summary>
    /// The development machine's culture. Its decimal separator is a comma, which is exactly what would
    /// corrupt a comma-delimited file if any formatting were culture-sensitive.
    /// </summary>
    private static readonly CultureInfo CommaDecimal = CultureInfo.GetCultureInfo("uk-UA");

    private static MonitorSnapshot BuildSnapshot()
    {
        Hist h = Hist.New()
            .App(1, @"C:\Program Files\Some, App\thing.exe")
            .App(2, @"D:\envs\Corpus\python.exe");

        h.At(0, (1, 250.5), (2, 3072));
        h.At(10, (1, null), (2, 3072));       // present but unreadable
        h.Failed(20);
        h.At(30, (2, 3072));                  // session 1 exited

        // A deliberately fractional interval, so culture-sensitive number formatting would be visible.
        MonitorSettings settings = new MonitorSettings
        {
            MonitoringFloorBytes = Mb(100),
            SampleInterval = TimeSpan.FromSeconds(12.5),
        }.Validated();
        AnalysisResult analysis = Analyzer.Analyze(h.Samples, h.Sessions, settings, [], h.Now);

        return new MonitorSnapshot(
            new GpuInfo(new GpuId("luid_0x0_0x1AEA2"), new GpuSelector(0x10DE, 0x2204, 0, "NVIDIA GeForce RTX 3090", 0),
                "NVIDIA GeForce RTX 3090", 24L * 1024 * 1024 * 1024, false),
            settings,
            h.Now,
            h.Samples,
            h.Sessions,
            analysis,
            MonitorHealth.Empty with { FailedProbeCount = 1, PartialProbeCount = 1 });
    }

    private static T InCulture<T>(CultureInfo culture, Func<T> action)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Json_export_round_trips_and_preserves_missing_as_null()
    {
        string json = JsonExporter.ToJson(BuildSnapshot());

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(JsonExporter.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("NVIDIA GeForce RTX 3090", root.GetProperty("gpu").GetProperty("description").GetString());

        JsonElement samples = root.GetProperty("samples");
        Assert.Equal(4, samples.GetArrayLength());

        // The unreadable observation must be null, never zero.
        JsonElement second = samples[1].GetProperty("observations");
        JsonElement missing = second.EnumerateArray().First(o => o.GetProperty("sessionId").GetInt64() == 1);
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("dedicatedBytes").ValueKind);

        // The failed probe is recorded with its outcome and no observations.
        Assert.Equal("Failed", samples[2].GetProperty("outcome").GetString());
        Assert.Equal(0, samples[2].GetProperty("observations").GetArrayLength());
    }

    [Fact]
    public void Json_export_is_culture_invariant()
    {
        string underComma = InCulture(CommaDecimal, () => JsonExporter.ToJson(BuildSnapshot()));
        string underInvariant = InCulture(CultureInfo.InvariantCulture, () => JsonExporter.ToJson(BuildSnapshot()));

        Assert.Equal(underInvariant, underComma);

        // The fractional interval must serialise with a decimal point, not the culture's comma.
        Assert.Contains("\"sampleIntervalSeconds\": 12.5", underComma, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_observations_use_an_invariant_decimal_point_under_a_comma_decimal_culture()
    {
        string csv = InCulture(CommaDecimal, () => CsvExporter.WriteObservations(BuildSnapshot()));

        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(11, lines[0].Split(',').Length);

        // Every data row must have exactly the header's column count once quoted fields are accounted for.
        foreach (string line in lines.Skip(1))
        {
            Assert.Equal(11, CountFields(line));
        }
    }

    [Fact]
    public void Csv_writes_a_missing_measurement_as_an_empty_field_not_a_zero()
    {
        string csv = InCulture(CommaDecimal, () => CsvExporter.WriteObservations(BuildSnapshot()));

        string[][] rows = [.. csv
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(SplitFields)];

        // Columns: 5 = session_id, 8 = dedicated_bytes, 9 = shared_bytes (0-based).
        string[] missing = rows.Single(r => r[5] == "1" && r[1] == "Partial");
        Assert.Equal(string.Empty, missing[8]);

        // And the measured observation in the same export does carry a value, so the empty field above is
        // genuinely "missing" rather than the exporter simply never writing numbers.
        string[] measured = rows.Single(r => r[5] == "1" && r[1] == "Ok");
        Assert.Equal(Mb(250.5).ToString(CultureInfo.InvariantCulture), measured[8]);
    }

    [Fact]
    public void Csv_quotes_a_path_containing_a_comma()
    {
        string csv = CsvExporter.WriteObservations(BuildSnapshot());

        Assert.Contains("\"C:\\Program Files\\Some, App\\thing.exe\"", csv, StringComparison.Ordinal);
        foreach (string line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            Assert.Equal(11, CountFields(line));
        }
    }

    [Fact]
    public void Application_summaries_live_in_their_own_file_with_their_own_row_shape()
    {
        string csv = InCulture(CommaDecimal, () => CsvExporter.WriteApplications(BuildSnapshot()));
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(9, lines[0].Split(',').Length);
        Assert.Contains("application_key", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("timestamp_utc", lines[0], StringComparison.Ordinal);

        foreach (string line in lines.Skip(1)) Assert.Equal(9, CountFields(line));
    }

    [Fact]
    public void Export_covers_every_retained_consumer_not_only_aggressive_ones()
    {
        MonitorSnapshot snapshot = BuildSnapshot();
        string csv = CsvExporter.WriteApplications(snapshot);

        Assert.Contains("thing.exe", csv, StringComparison.Ordinal);   // exited mid-window
        Assert.Contains("python.exe", csv, StringComparison.Ordinal);
    }

    private static int CountFields(string line) => SplitFields(line).Length;

    private static string[] SplitFields(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes) { fields.Add(current.ToString()); current.Clear(); }
            else if (c != '\r') current.Append(c);
        }

        fields.Add(current.ToString());
        return [.. fields];
    }
}
