using System.Text.Json;
using Shared.Contracts;

namespace Master.Pipelines.Implementations;

public class DnaAeonPipeline : AbstractPipeline
{
    public override string Version => "1";

    protected override async Task BuildAsync(JsonElement parameters)
    {
        var data = await AddUploadAsync("D");

        int encode = await AddJobAsync("encode", JobTypes.DnaAeonEncode, parameters);
        await LinkInputAsync(encode, "D", data);

        int decode = await AddJobAsync("decode", JobTypes.DnaAeonDecode, parameters);
        await LinkInputAsync(decode, "originalData", data);
        await LinkInputAsync(decode, "encoded.fasta", await OutputOfAsync(encode, "encoded.fasta"));
        await LinkInputAsync(decode, "encoded.ini", await OutputOfAsync(encode, "encoded.ini"));
    }
}
