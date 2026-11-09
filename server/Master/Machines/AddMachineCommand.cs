using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace Master.Machines;

public static class AddMachineCommand
{
    public static async Task RunAsync(string connectionString)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        await using var db = NpgsqlDataSource.Create(connectionString);
        await using var connection = await db.OpenConnectionAsync();
        var id = await connection.QuerySingleAsync<Guid>(
            "INSERT INTO machines (token_hash) VALUES (@hash) RETURNING id",
            new { hash });

        Console.WriteLine($"Token for machine {id} (shown once): {token}");
    }
}
