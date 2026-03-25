using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using WatchBack.Api.Auth;
using WatchBack.Core.Options;
using WatchBack.Resources;

namespace WatchBack.Api.Endpoints;

public static class AuthEndpoints
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    internal static readonly SemaphoreSlim ConfigFileLock = new(1, 1);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth")
            .WithTags("Authentication");

        group.MapGet("/me", GetMe)
            .WithName("GetMe")
            .WithSummary("Get current authentication status")
            .AllowAnonymous();

        group.MapPost("/login", Login)
            .WithName("Login")
            .WithSummary("Log in with username and password")
            .AllowAnonymous()
            .RequireRateLimiting("login");

        group.MapPost("/logout", (Delegate)Logout)
            .WithName("Logout")
            .WithSummary("Log out and clear session cookie")
            .RequireAuthorization();

        group.MapPost("/setup", Setup)
            .WithName("Setup")
            .WithSummary("Complete onboarding by setting a permanent username and password")
            .RequireAuthorization();

        group.MapPost("/reset-password", ResetPassword)
            .WithName("ResetPassword")
            .WithSummary("Generate and store a new random password, logged to stdout")
            .RequireAuthorization();

        group.MapPost("/change-password", ChangePassword)
            .WithName("ChangePassword")
            .WithSummary("Change the current user's password")
            .RequireAuthorization();

        group.MapPost("/forward-auth", SaveForwardAuth)
            .WithName("SaveForwardAuth")
            .WithSummary("Enable or disable forward auth header bypass")
            .RequireAuthorization();
    }

    private static IResult GetMe(HttpContext ctx, IOptionsSnapshot<AuthOptions> authOptions)
    {
        var user = ctx.User;
        var isAuthenticated = user.Identity?.IsAuthenticated == true;
        var authMethod = isAuthenticated
            ? (user.Identity?.AuthenticationType == "ForwardAuth" ? "forwardAuth" : "cookie")
            : (string?)null;
        var username = isAuthenticated ? user.Identity?.Name : null;
        var opts = authOptions.Value;
        var needsOnboarding = authMethod != "forwardAuth" && !opts.OnboardingComplete;

        return Results.Ok(new
        {
            authenticated = isAuthenticated,
            username,
            needsOnboarding,
            authMethod,
            forwardAuthHeader = opts.ForwardAuthHeader ?? "",
            forwardAuthTrustedHost = opts.ForwardAuthTrustedHost ?? "",
        });
    }

    private static async Task<IResult> Login(
        HttpContext ctx,
        [FromBody] LoginRequest body,
        IOptionsSnapshot<AuthOptions> authOptions,
        CancellationToken ct)
    {
        var opts = authOptions.Value;

        if (string.IsNullOrEmpty(opts.PasswordHash))
            return Results.Ok(new { ok = false, message = "Authentication not configured. Check server logs." });

        if (string.IsNullOrEmpty(body.Username) || string.IsNullOrEmpty(body.Password))
            return Results.Ok(new { ok = false, message = "Username and password are required." });

        if (!string.Equals(body.Username, opts.Username, StringComparison.OrdinalIgnoreCase))
            return Results.Ok(new { ok = false, message = "Invalid credentials." });

        var hasher = new PasswordHasher<string>();
        var result = hasher.VerifyHashedPassword("", opts.PasswordHash, body.Password);

        if (result == PasswordVerificationResult.Failed)
            return Results.Ok(new { ok = false, message = "Invalid credentials." });

        // Upgrade the stored hash if the hashing algorithm has been updated
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var configFile = ctx.RequestServices.GetRequiredService<UserConfigFile>();
            var newHash = hasher.HashPassword("", body.Password);
            await WriteAuthConfig(configFile, opts.Username, newHash, opts.OnboardingComplete, ct);
        }

        await SignInUser(ctx, body.Username);

        var needsOnboarding = !opts.OnboardingComplete;
        var needsPasswordChange = result == PasswordVerificationResult.SuccessRehashNeeded;
        return Results.Ok(new { ok = true, needsOnboarding, needsPasswordChange, message = (string?)null });
    }

    private static async Task<IResult> Logout(HttpContext ctx)
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Ok();
    }

    private static async Task<IResult> Setup(
        HttpContext ctx,
        [FromBody] SetupRequest body,
        IOptionsSnapshot<AuthOptions> authOptions,
        UserConfigFile configFile,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.NewUsername))
            return Results.Ok(new { ok = false, message = "New username is required." });

        if (string.IsNullOrWhiteSpace(body.NewPassword))
            return Results.Ok(new { ok = false, message = "New password is required." });

        var currentUsername = authOptions.Value.Username;
        if (string.Equals(body.NewUsername, currentUsername, StringComparison.OrdinalIgnoreCase))
            return Results.Ok(new { ok = false, message = "New username must be different from the current username." });

        var hasher = new PasswordHasher<string>();
        var newHash = hasher.HashPassword("", body.NewPassword);

        await WriteAuthConfig(configFile, body.NewUsername, newHash, onboardingComplete: true, ct);

        // Re-sign-in with new username so session reflects new identity
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await SignInUser(ctx, body.NewUsername);

        return Results.Ok(new { ok = true, message = (string?)null });
    }

    private static async Task<IResult> ResetPassword(
        HttpContext ctx,
        UserConfigFile configFile,
        IOptionsSnapshot<AuthOptions> authOptions,
        CancellationToken ct)
    {
        var newPassword = GeneratePassword();
        var hasher = new PasswordHasher<string>();
        var newHash = hasher.HashPassword("", newPassword);

        var currentUsername = authOptions.Value.Username;
        var onboardingComplete = authOptions.Value.OnboardingComplete;

        await WriteAuthConfig(configFile, currentUsername, newHash, onboardingComplete, ct);

        // Write directly to stdout — avoids logger pipeline so credentials
        // don't end up in structured log sinks (Seq, ELK, etc.)
        Console.WriteLine("╔══════════════════════════════════════════════╗");
        Console.WriteLine("║     WatchBack — Password Reset               ║");
        Console.WriteLine($"║  Username : {currentUsername,-36}║");
        Console.WriteLine($"║  Password : {newPassword,-36}║");
        Console.WriteLine("║  Use this password to log in.                ║");
        Console.WriteLine("╚══════════════════════════════════════════════╝");

        return Results.Ok(new { ok = true, message = UiStrings.AuthEndpoints_ResetPassword_New_password_generated__Check_server_logs_ });
    }

    private static async Task<IResult> ChangePassword(
        HttpContext ctx,
        [FromBody] ChangePasswordRequest body,
        IOptionsSnapshot<AuthOptions> authOptions,
        UserConfigFile configFile,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.NewPassword))
            return Results.Ok(new { ok = false, message = UiStrings.AuthEndpoints_ChangePassword_Password_is_required_ });

        var opts = authOptions.Value;
        var hasher = new PasswordHasher<string>();
        var newHash = hasher.HashPassword("", body.NewPassword);

        await WriteAuthConfig(configFile, opts.Username, newHash, opts.OnboardingComplete, ct);

        // Re-sign-in so the session reflects the updated credential state
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await SignInUser(ctx, opts.Username);

        return Results.Ok(new { ok = true, message = (string?)null });
    }

    private static async Task<IResult> SaveForwardAuth(
        [FromBody] ForwardAuthSettingsRequest body,
        UserConfigFile configFile,
        CancellationToken ct)
    {
        await ConfigFileLock.WaitAsync(ct);
        try
        {
            var existing = await ReadConfigFile(configFile.Path, ct);

            if (!existing.ContainsKey("Auth"))
                existing["Auth"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            existing["Auth"]["ForwardAuthHeader"] = body.Header?.Trim() ?? "";
            existing["Auth"]["ForwardAuthTrustedHost"] = body.TrustedHost?.Trim() ?? "";

            await WriteConfigFile(configFile.Path, existing, ct);
        }
        finally
        {
            ConfigFileLock.Release();
        }

        return Results.Ok(new { ok = true });
    }

    private static Task SignInUser(HttpContext ctx, string username)
    {
        var claims = new[] { new Claim(ClaimTypes.Name, username) };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    }

    internal static async Task WriteAuthConfig(
        UserConfigFile configFile,
        string username,
        string passwordHash,
        bool onboardingComplete,
        CancellationToken ct)
    {
        await ConfigFileLock.WaitAsync(ct);
        try
        {
            var existing = await ReadConfigFile(configFile.Path, ct);

            // Merge into existing to preserve ForwardAuthHeader
            if (!existing.ContainsKey("Auth"))
                existing["Auth"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            existing["Auth"]["Username"] = username;
            existing["Auth"]["PasswordHash"] = passwordHash;
            existing["Auth"]["OnboardingComplete"] = onboardingComplete ? "True" : "False";

            await WriteConfigFile(configFile.Path, existing, ct);
        }
        finally
        {
            ConfigFileLock.Release();
        }
    }

    internal static async Task<Dictionary<string, Dictionary<string, string>>> ReadConfigFile(
        string path, CancellationToken ct)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
            if (parsed != null)
                foreach (var (section, values) in parsed)
                    result[section] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Log but continue — treat as empty config so the app can still start
            System.Diagnostics.Debug.WriteLine($"Failed to read config file {path}: {ex.Message}");
        }

        return result;
    }

    internal static async Task WriteConfigFile(
        string path, Dictionary<string, Dictionary<string, string>> data, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(data, s_jsonOptions);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
    }

    internal static string GeneratePassword(int length = 24)
    {
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string digits = "0123456789";
        const string special = "!@#$%^&*-_+=";
        const string charset = upper + lower + digits + special;

        // Rejection sampling — avoids modulo bias
        var maxUsable = (256 / charset.Length) * charset.Length; // largest multiple of charset.Length ≤ 256

        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            int b;
            do { b = RandomNumberGenerator.GetBytes(1)[0]; }
            while (b >= maxUsable);
            chars[i] = charset[b % charset.Length];
        }

        // Guarantee at least one of each category
        Span<int> required = [
            RandomIndex(upper),
            RandomIndex(lower),
            RandomIndex(digits),
            RandomIndex(special)
        ];
        // Place required chars at random distinct positions
        var positions = Enumerable.Range(0, length).OrderBy(_ => RandomNumberGenerator.GetInt32(length)).Take(4).ToArray();
        var categories = new[] { upper, lower, digits, special };
        for (var i = 0; i < 4; i++)
            chars[positions[i]] = categories[i][required[i]];

        return new string(chars);

        static int RandomIndex(string pool) => RandomNumberGenerator.GetInt32(pool.Length);
    }

    private sealed record LoginRequest(string Username, string Password);
    private sealed record SetupRequest(string NewUsername, string NewPassword);
    private sealed record ChangePasswordRequest(string NewPassword);
    private sealed record ForwardAuthSettingsRequest(string? Header, string? TrustedHost);
}
