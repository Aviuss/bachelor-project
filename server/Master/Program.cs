using Master.Endpoints.Private;
using Master.Endpoints.Public;
using Master.Agent;
using Npgsql;
using Shared;

namespace Master;

public class Program
{
    public static async Task Main(string[] args)
    {
        if (args is ["add-machine"])
        {
            var config = WebApplication.CreateBuilder(args).Configuration;
            await AddMachineCommand.RunAsync(BuildPostgresConnectionString(config));
            return;
        }

        var agentApp = BuildApi(args, "AGENT_API_PORT", 5081);
        agentApp.MapAgentApi();

        var publicApp = BuildApi(args, "PUBLIC_API_PORT", 5080);
        publicApp.MapPublicApi();

        await agentApp.StartAsync();
        await publicApp.StartAsync();
        
        await Task.WhenAll(publicApp.WaitForShutdownAsync(), agentApp.WaitForShutdownAsync());
    }

    private static WebApplication BuildApi(string[] args, string portKey, int defaultPort)
    {
        var builder = WebApplication.CreateBuilder(args);

        var port = builder.Configuration.GetValue(portKey, defaultPort);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port));

        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(BuildPostgresConnectionString(builder.Configuration)));

        builder.Services.AddSingleton(new S3manager(
            endpoint: Required(builder.Configuration, "AWS_ENDPOINT_URL_S3"),
            region: Required(builder.Configuration, "AWS_DEFAULT_REGION"),
            accessKey: Required(builder.Configuration, "AWS_ACCESS_KEY_ID"),
            secretKey: Required(builder.Configuration, "AWS_SECRET_ACCESS_KEY"),
            bucket: Required(builder.Configuration, "S3_BUCKET")));

        builder.Services.AddOpenApi();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseAuthorization();

        return app;
    }

    private static string BuildPostgresConnectionString(IConfiguration config) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = Required(config, "POSTGRES_HOST"),
            Port = int.Parse(Required(config, "POSTGRES_PORT")),
            Database = Required(config, "POSTGRES_DB"),
            Username = Required(config, "POSTGRES_USER"),
            Password = Required(config, "POSTGRES_PASSWORD"),
            MaxPoolSize = 40,
        }.ConnectionString;

    private static string Required(IConfiguration config, string key) =>
        config[key] ?? throw new InvalidOperationException($"{key} is not configured.");
}
