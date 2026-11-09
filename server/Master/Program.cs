using Master.Endpoints;
using Agent;
using Npgsql;
using Shared;

namespace Master;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        if (args is ["add-machine"])
        {
            await AddMachineCommand.RunAsync(BuildConnectionString(builder.Configuration));
            return;
        }

        var publicPort = builder.Configuration.GetValue("PUBLIC_API_PORT", 5080);
        var agentPort = builder.Configuration.GetValue("AGENT_API_PORT", 5081);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(publicPort);
            kestrel.ListenAnyIP(agentPort);
        });

        // Add services to the container.
        builder.Services.AddAuthorization();

        AddStorage(builder.Services, builder.Configuration);

        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseAuthorization();

        app.MapPublicApi();
        app.MapAgentApi();

        await app.RunAsync();
    }

    private static void AddStorage(IServiceCollection services, IConfiguration config)
    {
        var connectionString = BuildConnectionString(config);
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

        services.AddSingleton(new S3manager(
            endpoint: Required(config, "AWS_ENDPOINT_URL_S3"),
            region: Required(config, "AWS_DEFAULT_REGION"),
            accessKey: Required(config, "AWS_ACCESS_KEY_ID"),
            secretKey: Required(config, "AWS_SECRET_ACCESS_KEY"),
            bucket: Required(config, "S3_BUCKET")));
    }

    private static string BuildConnectionString(IConfiguration config) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = Required(config, "POSTGRES_HOST"),
            Port = int.Parse(Required(config, "POSTGRES_PORT")),
            Database = Required(config, "POSTGRES_DB"),
            Username = Required(config, "POSTGRES_USER"),
            Password = Required(config, "POSTGRES_PASSWORD"),
        }.ConnectionString;

    private static string Required(IConfiguration config, string key) =>
        config[key] ?? throw new InvalidOperationException($"{key} is not configured.");
}
