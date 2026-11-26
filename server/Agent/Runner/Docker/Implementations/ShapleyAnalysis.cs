using System;
using Shared.Contracts;
using System.Text.Json;


namespace Agent.Runner.Docker.Implementations;


public class ShapleyAnalysis : DockerRunner
{
    protected override bool UseFilesForDbResources => false;

    public ShapleyAnalysis(Assignment assignment) : base(assignment, "shapleyanalysis:v1")
    {
        ContainerInputDir = "indir";
        ContainerOutputDir = "outdir";
        Cmd = ["cmd stuff"];
    }

    protected override Task PreRun(CancellationToken cancellationToken)
    {
        List<JsonElement> decodingResults = InputResources.OfType<DbResource>().Select(e => e.GetJson()).ToList();
        cancellationToken.ThrowIfCancellationRequested();

        if (InputPath is null)
            throw new InvalidOperationException("Input folder has not been prepared.");

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("codec,synthesis,sequencing,pcr,decay,1_minus_recovery_rate");
        var aggregates = new Dictionary<
            (string Codec, int Synthesis, int Sequencing, int Pcr, int Decay),
            (double FailureRateSum, int Count)>();

        foreach (JsonElement decodingResult in decodingResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string codec = ReadRequiredString(decodingResult, "codec");
            int synthesis = ReadBinaryValue(decodingResult, "synthesis");
            int sequencing = ReadBinaryValue(decodingResult, "sequencing");
            int pcr = ReadBinaryValue(decodingResult, "pcr");
            int decay = ReadBinaryValue(decodingResult, "decay");
            double recoveryRate = ReadResult(decodingResult, "result");
            var key = (codec, synthesis, sequencing, pcr, decay);

            aggregates.TryGetValue(key, out var aggregate);
            aggregates[key] = (aggregate.FailureRateSum + (1d - recoveryRate), aggregate.Count + 1);
        }

        foreach (var (key, aggregate) in aggregates
                     .OrderBy(item => item.Key.Codec, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Synthesis)
                     .ThenBy(item => item.Key.Sequencing)
                     .ThenBy(item => item.Key.Pcr)
                     .ThenBy(item => item.Key.Decay))
        {
            csv.Append(CsvEscape(key.Codec)).Append(',')
                .Append(key.Synthesis.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(key.Sequencing.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(key.Pcr.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(key.Decay.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append((aggregate.FailureRateSum / aggregate.Count).ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .AppendLine();
        }

        string csvPath = Path.Combine(InputPath, "sample_data.csv");
        File.WriteAllText(csvPath, csv.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Task.CompletedTask;

        static string ReadRequiredString(JsonElement record, string propertyName)
        {
            if (!record.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
                throw new System.IO.InvalidDataException($"Decoding result is missing string property '{propertyName}'.");

            return value.GetString()!;
        }

        static int ReadBinaryValue(JsonElement record, string propertyName)
        {
            if (!record.TryGetProperty(propertyName, out JsonElement value))
                throw new System.IO.InvalidDataException($"Decoding result is missing property '{propertyName}'.");

            int parsed = value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out int number) => number,
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                JsonValueKind.String when int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int number) => number,
                _ => throw new System.IO.InvalidDataException($"Property '{propertyName}' must be 0 or 1."),
            };

            if (parsed is not (0 or 1))
                throw new System.IO.InvalidDataException($"Property '{propertyName}' must be 0 or 1, got {parsed}.");

            return parsed;
        }

        static double ReadResult(JsonElement record, string propertyName)
        {
            if (!record.TryGetProperty(propertyName, out JsonElement value))
                throw new System.IO.InvalidDataException($"Decoding result is missing property '{propertyName}'.");

            double parsed = value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetDouble(out double number) => number,
                JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) => number,
                _ => throw new System.IO.InvalidDataException($"Property '{propertyName}' must be a number between 0 and 1."),
            };

            if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < 0 || parsed > 1)
                throw new System.IO.InvalidDataException($"Property '{propertyName}' must be between 0 and 1, got {parsed}.");

            return parsed;
        }

        static string CsvEscape(string value) =>
            value.IndexOfAny([',', '"', '\r', '\n']) < 0
                ? value
                : $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

