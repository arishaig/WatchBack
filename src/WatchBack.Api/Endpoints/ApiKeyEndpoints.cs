using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using WatchBack.Infrastructure.Persistence;
using WatchBack.Infrastructure.Persistence.Entities;

namespace WatchBack.Api.Endpoints;

public static class ApiKeyEndpoints
{
    public static void MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/keys")
            .WithTags("API Keys");

        group.MapGet("/", ListKeys)
            .WithName("ListApiKeys")
            .WithSummary("List all MCP API keys")
            .Produces(StatusCodes.Status200OK);

        group.MapPost("/", GenerateKey)
            .WithName("GenerateApiKey")
            .WithSummary("Generate a new MCP API key")
            .Accepts<GenerateKeyRequest>("application/json")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapDelete("/{id:int}", RevokeKey)
            .WithName("RevokeApiKey")
            .WithSummary("Revoke an MCP API key")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListKeys(WatchBackDbContext db, CancellationToken ct)
    {
        List<object> keys = await db.ApiKeys
            .OrderBy(k => k.CreatedAt)
            .Select(k => (object)new { k.Id, k.Name, k.Prefix, k.CreatedAt })
            .ToListAsync(ct);

        return Results.Ok(keys);
    }

    private static async Task<IResult> GenerateKey(
        HttpContext ctx,
        WatchBackDbContext db,
        CancellationToken ct)
    {
        GenerateKeyRequest? body = await ctx.Request.ReadFromJsonAsync<GenerateKeyRequest>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Name))
        {
            return Results.BadRequest("Name is required.");
        }

        // 32 random bytes → base64url → prefix with "wb_"
        byte[] randomBytes = RandomNumberGenerator.GetBytes(32);
        string keyBody = Convert.ToBase64String(randomBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        string key = "wb_" + keyBody;
        string prefix = key[..Math.Min(12, key.Length)];

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        ApiKeyEntity entity = new()
        {
            Name = body.Name.Trim(),
            KeyHash = hash,
            Prefix = prefix,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/keys/{entity.Id}", new
        {
            entity.Id,
            entity.Name,
            entity.Prefix,
            entity.CreatedAt,
            Key = key
        });
    }

    private static async Task<IResult> RevokeKey(
        [FromRoute] int id,
        WatchBackDbContext db,
        CancellationToken ct)
    {
        ApiKeyEntity? entity = await db.ApiKeys.FindAsync([id], ct);
        if (entity is null)
        {
            return Results.NotFound();
        }

        db.ApiKeys.Remove(entity);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private sealed record GenerateKeyRequest(string Name);
}
