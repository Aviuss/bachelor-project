using Amazon.Runtime;
using Amazon.S3;
using Npgsql;

namespace Master;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

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

        app.UseHttpsRedirection();

        app.UseAuthorization();

        var summaries = new[]
        {
            "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        };

        app.MapGet("/weatherforecast", (HttpContext httpContext) =>
        {
            var forecast =  Enumerable.Range(1, 5).Select(index =>
                new WeatherForecast
                {
                    Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                    TemperatureC = Random.Shared.Next(-20, 55),
                    Summary = summaries[Random.Shared.Next(summaries.Length)]
                })
                .ToArray();
            return forecast;
        })
        .WithName("GetWeatherForecast");

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

        var s3Credentials = new BasicAWSCredentials(Required("AWS_ACCESS_KEY_ID"), Required("AWS_SECRET_ACCESS_KEY"));
        var s3Config = new AmazonS3Config
        {
            ServiceURL = Required("AWS_ENDPOINT_URL_S3"),
            AuthenticationRegion = Required("AWS_DEFAULT_REGION"),
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(s3Credentials, s3Config));
    }
}
