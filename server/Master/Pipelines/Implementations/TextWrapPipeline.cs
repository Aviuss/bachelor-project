using System.Text.Json;
using Shared.Contracts;

namespace Master.Pipelines.Implementations;

public class TextWrapPipeline : AbstractPipeline
{
    public override string Version => "1";

    protected override async Task BuildAsync(JsonElement parameters)
    {
        var text = await AddUploadAsync("text");

        int append = await AddJobAsync("append", JobTypes.AppendText, parameters.GetProperty("append"));
        await LinkInputAsync(append, "text", text);

        int prepend = await AddJobAsync("prepend", JobTypes.PrependText, parameters.GetProperty("prepend"));
        await LinkInputAsync(prepend, "text", await OutputOfAsync(append, "text"));
    }
}
