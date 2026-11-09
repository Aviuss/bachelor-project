using Master.Endpoints;
using Npgsql;
using Shared;

namespace Master;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

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

        app.Run();
    }

    private static void AddStorage(IServiceCollection services, IConfiguration config)
    {
        string Required(string key) =>
            config[key] ?? throw new InvalidOperationException($"{key} is not configured.");

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = Required("POSTGRES_HOST"),
            Port = int.Parse(Required("POSTGRES_PORT")),
            Database = Required("POSTGRES_DB"),
            Username = Required("POSTGRES_USER"),
            Password = Required("POSTGRES_PASSWORD"),
        }.ConnectionString;
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

        services.AddSingleton(new S3manager(
            endpoint: Required("AWS_ENDPOINT_URL_S3"),
            region: Required("AWS_DEFAULT_REGION"),
            accessKey: Required("AWS_ACCESS_KEY_ID"),
            secretKey: Required("AWS_SECRET_ACCESS_KEY"),
            bucket: Required("S3_BUCKET")));
    }
}
